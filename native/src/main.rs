//! Serialization shim only. Catalog, validation policy, selection, conflicts,
//! transactions and patcher lifecycle are implemented by the C# application.
use std::{fs::File, io::{self, Read}};
use camino::Utf8PathBuf;
use serde_json::{Value, json};
use ltk_mod_project::{ProjectImporter, ProjectPaths, fantome::FantomeImporter, modpkg::ModpkgImporter};
use ltk_overlay::{EnabledMod, FsModContent, OverlayBuilder};

fn arg<'a>(r: &'a Value, k: &str) -> Result<&'a str, Box<dyn std::error::Error>> {
    r[k].as_str().ok_or_else(|| format!("Missing string: {k}").into())
}
fn call(r: Value) -> Result<Value, Box<dyn std::error::Error>> {
    match arg(&r, "operation")? {
        "version" => Ok(json!({"protocol":1,"league_mod":"acaf19e5e1e32ff11376f0af1bccfbaecc28026d"})),
        "probe" => {
            let mut p = ltk_modpkg::Modpkg::mount_from_reader(File::open(arg(&r,"package")?)?)?;
            let project = ltk_mod_project::modpkg::read_project(&mut p)?;
            let paths: Vec<String> = p.extraction_plan().iter_project_paths().map(|p| p.to_string()).collect();
            let bytes: u64 = p.chunks().values().map(|c| c.uncompressed_size).sum();
            let overrides: Vec<String> = p.chunks().values().filter(|c| c.layer().is_some()).map(|c| c.path_hash.to_string()).collect();
            let records: Vec<Value> = p.chunks().values().map(|c| json!({"offset":c.data_offset,"compressed":c.compressed_size,"expanded":c.uncompressed_size,"checksum":c.compressed_checksum})).collect();
            Ok(json!({"project": project,"paths":paths,"bytes":bytes,"chunks":p.chunks().len(),"overrides":overrides,"records":records}))
        },
        "import" => {
            let path = arg(&r,"package")?;
            let out = arg(&r,"output")?;
            let file = File::open(path)?;
            let driver = ProjectImporter::new(out);
            let p = if path.to_ascii_lowercase().ends_with(".modpkg") {
                driver.import(ModpkgImporter::new(file))?
            } else { driver.import(FantomeImporter::new(file))? };
            Ok(json!({"project":p}))
        },
        "pack" => {
            let packer = ltk_mod_project::ProjectPacker::from_dir(arg(&r,"project")?)?;
            let format = ltk_mod_project::modpkg::ModpkgFormat::new(File::create(arg(&r,"output")?)?);
            packer.pack(format)?;
            Ok(json!({"packed":true}))
        },
        "build" => {
            let mut b = OverlayBuilder::new(Utf8PathBuf::from(arg(&r,"game")?),
                Utf8PathBuf::from(arg(&r,"overlay")?), Utf8PathBuf::from(arg(&r,"state")?))
                .with_progress(|p| println!("{}", json!({"progress":true,"stage":format!("{:?}",p.stage),"current":p.current,"total":p.total})));
            let mods = r["mods"].as_array().ok_or("Missing mods array")?.iter().map(|m| {
                Ok(EnabledMod { id:arg(m,"id")?.to_owned(), content: Box::new(FsModContent::new(Utf8PathBuf::from(arg(m,"path")?))), enabled_layers:None })
            }).collect::<Result<Vec<_>, Box<dyn std::error::Error>>>()?;
            b.set_enabled_mods(mods);
            let result = b.build()?;
            Ok(json!({"wads":result.wads_built.len()}))
        },
        op => Err(format!("Unknown shim operation {op}").into())
    }
}
fn main() {
    let result = std::panic::catch_unwind(|| {
        let mut s = String::new(); io::stdin().read_to_string(&mut s)?;
        call(serde_json::from_str(&s)?)
    });
    match result {
        Ok(Ok(v)) => println!("{}",json!({"ok":true,"result":v})),
        Ok(Err(e)) => { eprintln!("{e:?}"); println!("{}",json!({"ok":false,"error":e.to_string()})); std::process::exit(1); },
        Err(_) => { println!("{}",json!({"ok":false,"error":"Upstream library panicked while reading or building content"})); std::process::exit(2); }
    }
}
