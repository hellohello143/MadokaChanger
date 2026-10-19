using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using MessagePack;
using ZstdSharp;

namespace MadokaChanger.Core;

// Format v1 layout: pinned league-mod ltk_modpkg/{read,chunk,builder,metadata}.rs.
// Parse the tables, replace just the MsgPack name values, and relocate chunks by their declared offsets.
// All other metadata values and all non-metadata compressed bytes are retained verbatim.
internal static class ModpkgRewriter
{
    private const int RecordSize=61,MaximumMetadataBytes=16*1024*1024;
    private sealed record Layout(long Toc,byte[] Records,int Metadata,long Offset,int Length,int Expanded,byte Compression);

    public static async Task RewriteAsync(string source,string destination,string id,CancellationToken ct)
    {
        await using var input=File.OpenRead(source);
        var layout=ReadLayout(input,ct);
        input.Position=layout.Offset;
        var compressed=new byte[layout.Length];await input.ReadExactlyAsync(compressed,ct);
        var record=layout.Records.AsSpan(layout.Metadata*RecordSize,RecordSize);
        if(XxHash3.HashToUInt64(compressed)!=BinaryPrimitives.ReadUInt64LittleEndian(record[33..]))
            throw new InvalidDataException("modpkg metadata compressed checksum mismatch.");
        byte[] metadata;
        if(layout.Compression==0) metadata=compressed;
        else
        {
            using var decoder=new Decompressor();metadata=new byte[layout.Expanded];
            if(decoder.Unwrap(compressed,metadata)!=metadata.Length) throw new InvalidDataException("modpkg metadata expanded size mismatch.");
        }
        if(metadata.Length!=layout.Expanded || XxHash3.HashToUInt64(metadata)!=BinaryPrimitives.ReadUInt64LittleEndian(record[41..]))
            throw new InvalidDataException("modpkg metadata checksum mismatch.");
        var rewritten=RewriteMetadata(metadata,id,ct);
        byte[] stored;
        if(layout.Compression==0) stored=rewritten;
        else {using var encoder=new Compressor(3);stored=encoder.Wrap(rewritten).ToArray();}
        var delta=(long)stored.Length-layout.Length;
        var table=layout.Records.ToArray();
        for(var i=0;i<table.Length/RecordSize;i++)
        {
            var chunk=table.AsSpan(i*RecordSize,RecordSize);
            var offset=checked((long)BinaryPrimitives.ReadUInt64LittleEndian(chunk[8..]));
            if(i==layout.Metadata)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(chunk[17..],(ulong)stored.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(chunk[25..],(ulong)rewritten.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(chunk[33..],XxHash3.HashToUInt64(stored));
                BinaryPrimitives.WriteUInt64LittleEndian(chunk[41..],XxHash3.HashToUInt64(rewritten));
            }
            else if(offset>=layout.Offset+layout.Length)
                BinaryPrimitives.WriteUInt64LittleEndian(chunk[8..],checked((ulong)(offset+delta)));
        }
        await using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
        {
            input.Position=0;
            await CopyRangeAsync(input,output,layout.Offset,ct);
            await output.WriteAsync(stored,ct);
            input.Position=layout.Offset+layout.Length;
            await input.CopyToAsync(output,ct);
            output.Position=layout.Toc;await output.WriteAsync(table,ct);
            await output.FlushAsync(ct);
        }
        // Independently reread both files: the protected prefix and tail must match exactly, even unknown chunks/gaps.
        await using var check=File.OpenRead(destination);
        await EqualRangeAsync(input,check,0,0,layout.Toc,ct);
        var actualTable=new byte[table.Length];await check.ReadExactlyAsync(actualTable,ct);
        if(!actualTable.AsSpan().SequenceEqual(table)) throw new InvalidDataException("modpkg rewrite changed its chunk table unexpectedly.");
        var end=layout.Toc+table.Length;
        await EqualRangeAsync(input,check,end,end,layout.Offset-end,ct);
        await EqualRangeAsync(input,check,layout.Offset+layout.Length,layout.Offset+stored.Length,input.Length-layout.Offset-layout.Length,ct);
        if(check.Length!=input.Length+delta) throw new InvalidDataException("modpkg rewrite changed protected file size.");
        check.Position=layout.Offset;var actualMetadata=new byte[stored.Length];await check.ReadExactlyAsync(actualMetadata,ct);
        if(!actualMetadata.AsSpan().SequenceEqual(stored)) throw new InvalidDataException("modpkg rewrite changed its metadata unexpectedly.");
    }

    private static Layout ReadLayout(Stream stream,CancellationToken ct)
    {
        using var reader=new BinaryReader(stream,new UTF8Encoding(false,true),true);
        if(!reader.ReadBytes(8).AsSpan().SequenceEqual("_modpkg_"u8) || reader.ReadUInt32()!=1) throw new InvalidDataException("Only modpkg format v1 is supported.");
        if(reader.ReadUInt32()!=0) throw new InvalidDataException("Signed modpkg packages cannot be rewritten without invalidating their signature.");
        var chunks=Count();
        var layers=Count();
        for(var i=0;i<layers;i++){ct.ThrowIfCancellationRequested();Skip(Count());Skip(4);}
        var paths=Count();var metadataIndex=-1;
        for(var i=0;i<paths;i++)
        {
            var name=Name();ArchiveSafety.ValidatePath(name);
            if(name.Replace('\\','/').Equals("_meta_/info.msgpack",StringComparison.OrdinalIgnoreCase))
            {
                if(metadataIndex!=-1) throw new InvalidDataException("Duplicate modpkg metadata path.");
                metadataIndex=i;
            }
        }
        var wads=Count();for(var i=0;i<wads;i++)_ = Name();
        var toc=(stream.Position+7)&~7L;
        var table=new byte[checked(chunks*RecordSize)];
        if(toc>stream.Length-table.Length) throw new InvalidDataException("modpkg table is truncated.");
        stream.Position=toc;stream.ReadExactly(table);
        var meta=-1;long start=0;int length=0,expanded=0;byte compression=0;
        for(var i=0;i<chunks;i++)
        {
            ct.ThrowIfCancellationRequested();var record=table.AsSpan(i*RecordSize,RecordSize);
            var offset=BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);var size=BinaryPrimitives.ReadUInt64LittleEndian(record[17..]);
            if(offset<(ulong)(toc+table.Length) || offset>(ulong)stream.Length || size>(ulong)stream.Length-offset)
                throw new InvalidDataException("modpkg chunk range is invalid.");
            if(BinaryPrimitives.ReadUInt32LittleEndian(record[49..])!=(uint)metadataIndex)continue;
            if(meta!=-1 || BinaryPrimitives.ReadUInt64LittleEndian(record)!=XxHash64.HashToUInt64("_meta_/info.msgpack"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[53..])!=uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(record[57..])!=uint.MaxValue)
                throw new InvalidDataException("modpkg metadata has an ambiguous identity.");
            var unpacked=BinaryPrimitives.ReadUInt64LittleEndian(record[25..]);
            if(size>MaximumMetadataBytes || unpacked>MaximumMetadataBytes || record[16]>1 || record[16]==0 && size!=unpacked)
                throw new InvalidDataException("Unsupported or oversized modpkg metadata (16 MB maximum).");
            meta=i;start=(long)offset;length=(int)size;expanded=(int)unpacked;compression=record[16];
        }
        if(meta<0 || length==0)throw new InvalidDataException("modpkg has no metadata chunk.");
        for(var i=0;i<chunks;i++)
        {
            if(i==meta)continue;var record=table.AsSpan(i*RecordSize,RecordSize);
            var offset=(long)BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);var size=(long)BinaryPrimitives.ReadUInt64LittleEndian(record[17..]);
            if(offset<start+length && offset+size>start) throw new InvalidDataException("modpkg metadata overlaps another chunk.");
        }
        return new(toc,table,meta,start,length,expanded,compression);

        int Count(){var n=reader.ReadUInt32();if(n>ArchiveSafety.MaximumEntries)throw new InvalidDataException("modpkg table exceeds limits.");return (int)n;}
        void Skip(long bytes){if(bytes>stream.Length-stream.Position)throw new InvalidDataException("modpkg header is truncated.");stream.Position+=bytes;}
        string Name()
        {
            ct.ThrowIfCancellationRequested();var bytes=new List<byte>();
            for(byte b;(b=reader.ReadByte())!=0;){if(bytes.Count>=1000)throw new InvalidDataException("modpkg name exceeds limits.");bytes.Add(b);}
            return new UTF8Encoding(false,true).GetString(bytes.ToArray());
        }
    }

    private static byte[] RewriteMetadata(byte[] metadata,string id,CancellationToken ct)
    {
        var reader=new MessagePackReader(metadata){CancellationToken=ct};
        var count=reader.ReadMapHeader();if(count>ArchiveSafety.MaximumEntries)throw new InvalidDataException("modpkg metadata has too many fields.");
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);writer.WriteMapHeader(count);
        var keys=new HashSet<string>(StringComparer.Ordinal);
        for(var i=0;i<count;i++)
        {
            ct.ThrowIfCancellationRequested();var key=reader.ReadString()??throw new InvalidDataException("modpkg metadata key is null.");
            if(!keys.Add(key))throw new InvalidDataException("Duplicate modpkg metadata key.");
            writer.Write(key);
            if(key is "name" or "display_name") { _=reader.ReadString();writer.Write(id); }
            else writer.WriteRaw(reader.ReadRaw()); // Keep unknown values, attribution and all functional declarations byte-for-byte.
        }
        if(!reader.End || !keys.Contains("name") || !keys.Contains("display_name"))throw new InvalidDataException("Unsupported modpkg metadata map.");
        writer.Flush();return buffer.WrittenSpan.ToArray();
    }

    private static async Task CopyRangeAsync(Stream input,Stream output,long length,CancellationToken ct)
    {
        var buffer=new byte[65536];
        while(length>0){var n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(length,buffer.Length)),ct);if(n==0)throw new EndOfStreamException();await output.WriteAsync(buffer.AsMemory(0,n),ct);length-=n;}
    }
    private static async Task EqualRangeAsync(Stream before,Stream after,long oldOffset,long newOffset,long length,CancellationToken ct)
    {
        before.Position=oldOffset;after.Position=newOffset;
        var a=new byte[65536];var b=new byte[65536];
        while(length>0)
        {
            var count=(int)Math.Min(length,a.Length);await before.ReadExactlyAsync(a.AsMemory(0,count),ct);await after.ReadExactlyAsync(b.AsMemory(0,count),ct);
            if(!a.AsSpan(0,count).SequenceEqual(b.AsSpan(0,count)))throw new InvalidDataException("modpkg rewrite changed protected payload bytes.");length-=count;
        }
    }
}
