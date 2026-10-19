using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MadokaChanger.Uninstaller;

internal sealed class MainForm : Form
{
    private static readonly Color Accent = ColorTranslator.FromHtml("#E86AA6");
    private static readonly Color AccentDark = ColorTranslator.FromHtml("#C94F8B");
    private static readonly Color Ink = ColorTranslator.FromHtml("#2B2230");
    private static readonly Color Muted = ColorTranslator.FromHtml("#7A6E80");
    private static readonly Color Surface = ColorTranslator.FromHtml("#FFF7FB");

    private readonly ListView list = new();
    private readonly CheckBox deleteData = new();
    private readonly Label result = new();
    private readonly Button uninstall = new();
    private readonly Button close = new();
    private ScanResult scan = new();
    private bool working;

    public int ExitCode { get; private set; }

    public MainForm()
    {
        Text = "MadokaChanger Uninstaller";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        ForeColor = Ink;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(640, 470);
        MinimumSize = new Size(520, 400);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Accent, Padding = new Padding(22, 14, 22, 10) };
        var subtitle = new Label { Text = "Removes every installed or portable MadokaChanger copy. Run it before installing the new version, not after.", Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f) };
        var title = new Label { Text = "Remove old MadokaChanger", Dock = DockStyle.Top, Height = 30, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15f) };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 16), ColumnCount = 1, RowCount = 6, BackColor = Color.White };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var foundLabel = new Label { Text = "Found on this PC", AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 0, 0, 6) };

        list.View = View.Details;
        list.FullRowSelect = true;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.BackColor = Surface;
        list.ForeColor = Ink;
        list.Dock = DockStyle.Fill;
        list.MultiSelect = false;
        list.ShowItemToolTips = true;
        list.Columns.Add("What", 140);
        list.Columns.Add("Where", 440);
        list.Resize += (_, _) => { if (list.Columns.Count == 2) list.Columns[1].Width = Math.Max(120, list.ClientSize.Width - list.Columns[0].Width - 4); };

        deleteData.Text = "Also delete my skins and settings";
        deleteData.Checked = false;
        deleteData.AutoSize = true;
        deleteData.Margin = new Padding(0, 12, 0, 0);
        deleteData.CheckedChanged += (_, _) => RenderList();
        var dataNote = new Label { AutoSize = true, ForeColor = Muted, Margin = new Padding(20, 0, 0, 0), Text = "Leave this off to keep your library, settings and patches for the new version." };

        result.AutoSize = true;
        result.MaximumSize = new Size(600, 0);
        result.Margin = new Padding(0, 12, 0, 0);
        result.ForeColor = Muted;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 14, 0, 0), WrapContents = false };
        Style(close, "Close", primary: false);
        Style(uninstall, "Uninstall", primary: true);
        close.Click += (_, _) => Close();
        uninstall.Click += async (_, _) => await UninstallAsync();
        buttons.Controls.Add(close);
        buttons.Controls.Add(uninstall);

        body.Controls.Add(foundLabel, 0, 0);
        body.Controls.Add(list, 0, 1);
        body.Controls.Add(deleteData, 0, 2);
        body.Controls.Add(dataNote, 0, 3);
        body.Controls.Add(result, 0, 4);
        body.Controls.Add(buttons, 0, 5);

        Controls.Add(body);
        Controls.Add(header);
        AcceptButton = uninstall;
        CancelButton = close;
        FormClosing += (_, e) => { if (working) e.Cancel = true; };
        Shown += async (_, _) => await RescanAsync();
    }

    private static void Style(Button b, string text, bool primary)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.Size = new Size(116, 36);
        b.Margin = new Padding(10, 0, 0, 0);
        b.Cursor = Cursors.Hand;
        b.Font = new Font("Segoe UI Semibold", 9.75f);
        b.FlatAppearance.BorderSize = 1;
        if (primary)
        {
            b.BackColor = Accent; b.ForeColor = Color.White; b.FlatAppearance.BorderColor = Accent;
            b.FlatAppearance.MouseOverBackColor = AccentDark; b.FlatAppearance.MouseDownBackColor = AccentDark;
        }
        else
        {
            b.BackColor = Color.White; b.ForeColor = Ink; b.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#D9CFDD");
            b.FlatAppearance.MouseOverBackColor = Surface;
        }
    }

    private async Task RescanAsync()
    {
        SetBusy(true, "Looking for old MadokaChanger copies...");
        scan = await Task.Run(Scanner.Scan);
        SetBusy(false, null);
        RenderList();
        if (!scan.HasProgramItems)
            SetResult(scan.DataFolders.Any() && !deleteData.Checked
                ? "No old MadokaChanger program found. You're ready to install the new version."
                : "No old MadokaChanger found. You're ready to install the new version.", ok: true);
        else SetResult("Click Uninstall to remove the items above.", ok: null);
    }

    private void RenderList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var item in scan.Items)
        {
            var row = new ListViewItem(item.KindLabel);
            var where = item.Kind == ItemKind.RunningApp ? item.Location : item.Title;
            string? note = null;
            if (item.RefusedBecause != null) note = "will not delete: " + item.RefusedBecause;
            else if (item.Kind == ItemKind.DataFolder && !deleteData.Checked) note = "kept";
            row.SubItems.Add(note is null ? where : where + "  (" + note + ")");
            row.ToolTipText = item.ToString();
            if (note != null) row.ForeColor = Muted;
            list.Items.Add(row);
        }
        if (scan.Items.Count == 0) list.Items.Add(new ListViewItem("Nothing") { ForeColor = Muted, SubItems = { "No MadokaChanger copies were found." } });
        list.EndUpdate();
        uninstall.Enabled = !working && (scan.HasProgramItems || (deleteData.Checked && scan.DataFolders.Any()));
    }

    private async Task UninstallAsync()
    {
        if (working) return;
        var running = scan.Items.Count(i => i.Kind == ItemKind.RunningApp);
        var message = "Remove the old MadokaChanger items listed here?";
        if (running > 0) message += "\n\nMadokaChanger is running. It will be closed first, so save anything you're doing in it.";
        message += deleteData.Checked
            ? "\n\nYour skins and settings will be deleted too. This can't be undone."
            : "\n\nYour skins and settings are kept.";
        if (MessageBox.Show(this, message, "MadokaChanger Uninstaller", MessageBoxButtons.YesNo, deleteData.Checked ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
        {
            SetResult("Nothing was changed.", ok: null);
            return;
        }

        var wipe = deleteData.Checked;
        SetBusy(true, "Removing...");
        var report = await Task.Run(() => Remover.Run(scan, wipe, s => BeginInvoke(new Action(() => result.Text = s))));
        SetBusy(false, null);
        ExitCode = report.Failed == 0 ? 0 : 1;

        scan = await Task.Run(Scanner.Scan);
        RenderList();
        if (report.Failed == 0)
            SetResult($"Done. Removed {report.Removed} item(s)." + (wipe ? "" : " Your skins and settings were kept.") + " You can install the new MadokaChanger now.", ok: true);
        else
            SetResult($"Removed {report.Removed}, but {report.Failed} could not be removed:\n" + string.Join("\n", report.Lines.Where(l => l.StartsWith("Failed")).Take(4)), ok: false);
    }

    private void SetBusy(bool busy, string? status)
    {
        working = busy;
        uninstall.Enabled = !busy;
        close.Enabled = !busy;
        deleteData.Enabled = !busy;
        UseWaitCursor = busy;
        if (status != null) SetResult(status, ok: null);
    }

    private void SetResult(string text, bool? ok)
    {
        result.Text = text;
        result.ForeColor = ok switch { true => ColorTranslator.FromHtml("#2E8B57"), false => ColorTranslator.FromHtml("#C0392B"), _ => Muted };
    }
}
