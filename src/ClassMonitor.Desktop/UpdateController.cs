using ClassMonitor.Shared.Updates;

namespace ClassMonitor.Desktop;

/// <summary>Non-blocking startup check; installing always requires clicking the update button.</summary>
internal sealed class UpdateController : IDisposable
{
    private readonly UpdateComponent _component;
    private readonly Control _owner;
    private readonly ToolStripItem _indicator;
    private readonly Action _exit;
    private readonly Action<string>? _notify;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateCheck? _last;
    private bool _checking;

    public UpdateController(UpdateComponent component, Control owner, ToolStripItem indicator,
        Action exit, Action<string>? notify = null)
    {
        _component = component;
        _owner = owner;
        _indicator = indicator;
        _exit = exit;
        _notify = notify;
        _indicator.Text = $"التحديثات / Updates · {ReleaseUpdater.CurrentVersion.ToString(3)}";
        _indicator.Click += (_, _) => OpenDialog();
    }

    public async Task CheckAtStartupAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            using var service = new ReleaseUpdater();
            var result = await service.CheckAsync(ReleaseUpdater.CurrentVersion, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || _owner.IsDisposed) return;
            _last = result;
            _indicator.Text = result.State switch
            {
                UpdateState.Available => $"تحديث {result.Release!.Version} متاح — اضغط / Update available",
                UpdateState.Current => $"محدّث / Up to date · {ReleaseUpdater.CurrentVersion.ToString(3)}",
                _ => "تعذّر فحص التحديثات — إعادة المحاولة / Retry update check"
            };
            if (result.State == UpdateState.Available) _notify?.Invoke(result.Message);
        }
        finally { _checking = false; }
    }

    private void OpenDialog()
    {
        using var dialog = new Form
        {
            Text = "تحديث ClassMonitor / Software update", ClientSize = new Size(580, 260),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen
        };
        var description = new Label
        {
            Dock = DockStyle.Top, Height = 65, Padding = new Padding(12),
            Text = $"الإصدار الحالي / Installed: {ReleaseUpdater.CurrentVersion.ToString(3)}\nفحص آخر إصدار مستقر على GitHub. / Latest stable release."
        };
        var status = new Label { Dock = DockStyle.Top, Height = 75, Padding = new Padding(12) };
        var progress = new ProgressBar { Dock = DockStyle.Top, Height = 20, Visible = false };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 65, Padding = new Padding(10) };
        var install = new Button { Text = "تنزيل وتثبيت وإعادة التشغيل / Install && restart", AutoSize = true, Enabled = false };
        var check = new Button { Text = "فحص / Check", AutoSize = true };
        buttons.Controls.AddRange(new Control[] { install, check });
        dialog.Controls.Add(buttons);
        dialog.Controls.Add(progress);
        dialog.Controls.Add(status);
        dialog.Controls.Add(description);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        bool busy = false;
        dialog.FormClosing += (_, _) => operation.Cancel();
        void Display()
        {
            status.Text = _last?.Message ?? "جارٍ فحص التحديثات / Checking for updates…";
            install.Enabled = _last?.State == UpdateState.Available;
        }
        async Task RefreshAsync()
        {
            if (busy) return;
            busy = true;
            check.Enabled = false;
            install.Enabled = false;
            status.Text = "جارٍ فحص التحديثات / Checking for updates…";
            try
            {
                using var service = new ReleaseUpdater();
                var result = await service.CheckAsync(ReleaseUpdater.CurrentVersion, operation.Token);
                if (dialog.IsDisposed || operation.IsCancellationRequested) return;
                _last = result;
                Display();
            }
            finally { busy = false; if (!dialog.IsDisposed) check.Enabled = true; }
        }
        check.Click += async (_, _) => await RefreshAsync();
        install.Click += async (_, _) =>
        {
            if (busy || _last?.Release is not { } release) return;
            busy = true;
            install.Enabled = check.Enabled = false;
            progress.Visible = true;
            status.Text = "جارٍ التنزيل والتحقق من الملفات. سيُعاد تشغيل البرنامج بعد التثبيت. / Downloading and verifying…";
            try
            {
                using var service = new ReleaseUpdater();
                var updates = new Progress<int>(value => { if (!dialog.IsDisposed) progress.Value = Math.Clamp(value, 0, 100); });
                var staged = await service.StageAsync(release, _component, updates, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                UpdateInstaller.Start(staged, Environment.ProcessPath ?? throw new IOException("Cannot locate the installed program."));
                dialog.Close();
                _exit();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!dialog.IsDisposed)
                    status.Text = $"لم يُثبّت التحديث. بقيت النسخة الحالية كما هي. / Update not installed.\n{ex.Message}";
            }
            finally
            {
                busy = false;
                if (!dialog.IsDisposed) { check.Enabled = true; install.Enabled = _last?.Release is not null; }
            }
        };
        Display();
        dialog.Shown += async (_, _) => await RefreshAsync();
        dialog.ShowDialog(_owner);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
