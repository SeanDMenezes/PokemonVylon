namespace PatchBuilderGUI;

sealed class MainForm : Form
{
    readonly TextBox _oldFolderBox = new();
    readonly TextBox _newFolderBox = new();
    readonly TextBox _fromVersionBox = new();
    readonly TextBox _toVersionBox = new();
    readonly Label _outputPreviewLabel = new();
    readonly TextBox _logBox = new();
    readonly Button _buildButton = new();
    readonly Button _openOutputButton = new();
    readonly ProgressBar _progressBar = new();

    string? _lastZipPath;
    CancellationTokenSource? _buildCts;

    public MainForm()
    {
        Text = "Pokemon Vylon Patch Builder";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 560);
        Size = new Size(820, 640);
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            AutoSize = false
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Margin = new Padding(0, 0, 0, 12),
            Text =
                "Build an update patch by picking the old live build folder and the new build folder, " +
                "then enter the version numbers. Upload the resulting .patch.zip and update-index.json to GitHub."
        };

        var foldersPanel = BuildFoldersPanel();
        var versionsPanel = BuildVersionsPanel();
        var actionsPanel = BuildActionsPanel();
        var logPanel = BuildLogPanel();
        var footerPanel = BuildFooterPanel();

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(foldersPanel, 0, 1);
        root.Controls.Add(versionsPanel, 0, 2);
        root.Controls.Add(actionsPanel, 0, 3);
        root.Controls.Add(logPanel, 0, 4);
        root.Controls.Add(footerPanel, 0, 5);

        Controls.Add(root);

        _fromVersionBox.TextChanged += (_, _) => RefreshOutputPreview();
        _toVersionBox.TextChanged += (_, _) => RefreshOutputPreview();
        _newFolderBox.TextChanged += (_, _) => RefreshOutputPreview();

        AcceptButton = _buildButton;
        RefreshOutputPreview();
        SetBusy(false);
    }

    Control BuildFoldersPanel()
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 12)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        AddFolderRow(
            panel,
            0,
            "Old build folder",
            "Folder for the version players currently have.",
            _oldFolderBox,
            () => BrowseForFolder(_oldFolderBox, "Select the OLD (current live) game folder"));

        AddFolderRow(
            panel,
            1,
            "New build folder",
            "Folder for the version you are shipping.",
            _newFolderBox,
            () => BrowseForFolder(_newFolderBox, "Select the NEW game folder"));

        return panel;
    }

    static void AddFolderRow(
        TableLayoutPanel panel,
        int row,
        string labelText,
        string helpText,
        TextBox pathBox,
        Action browse)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0)
        };

        pathBox.Dock = DockStyle.Fill;
        pathBox.Margin = new Padding(0, 4, 8, 4);
        pathBox.PlaceholderText = helpText;
        pathBox.AutoSize = false;

        var browseButton = new Button
        {
            Text = "Browse…",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
            AutoSize = false,
            FlatStyle = FlatStyle.System
        };
        browseButton.Click += (_, _) => browse();

        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(pathBox, 1, row);
        panel.Controls.Add(browseButton, 2, row);
    }

    Control BuildVersionsPanel()
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 2,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var fromLabel = new Label
        {
            Text = "From version",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0)
        };
        _fromVersionBox.Width = 120;
        _fromVersionBox.Margin = new Padding(0, 4, 16, 4);
        _fromVersionBox.PlaceholderText = "e.g. 1.0.0";

        var toLabel = new Label
        {
            Text = "To version",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0)
        };
        _toVersionBox.Width = 120;
        _toVersionBox.Margin = new Padding(0, 4, 8, 4);
        _toVersionBox.PlaceholderText = "e.g. 1.0.1";

        panel.Controls.Add(fromLabel, 0, 0);
        panel.Controls.Add(_fromVersionBox, 1, 0);
        panel.Controls.Add(toLabel, 2, 0);
        panel.Controls.Add(_toVersionBox, 3, 0);

        _outputPreviewLabel.AutoSize = true;
        _outputPreviewLabel.Margin = new Padding(0, 4, 0, 8);
        _outputPreviewLabel.ForeColor = Color.FromArgb(60, 60, 60);
        panel.SetColumnSpan(_outputPreviewLabel, 4);
        panel.Controls.Add(_outputPreviewLabel, 0, 1);

        return panel;
    }

    Control BuildActionsPanel()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8)
        };

        _buildButton.Text = "Build patch";
        _buildButton.AutoSize = true;
        _buildButton.Padding = new Padding(16, 6, 16, 6);
        _buildButton.Click += async (_, _) => await BuildPatchAsync();

        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.MarqueeAnimationSpeed = 30;
        _progressBar.Width = 220;
        _progressBar.Height = 22;
        _progressBar.Margin = new Padding(16, 10, 0, 0);
        _progressBar.Visible = false;

        panel.Controls.Add(_buildButton);
        panel.Controls.Add(_progressBar);
        return panel;
    }

    Control BuildLogPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };

        var label = new Label
        {
            Text = "Status",
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 4)
        };

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font("Consolas", 9.5f);
        _logBox.BackColor = Color.White;

        panel.Controls.Add(_logBox);
        panel.Controls.Add(label);
        return panel;
    }

    Control BuildFooterPanel()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _openOutputButton.Text = "Show patch zip";
        _openOutputButton.AutoSize = true;
        _openOutputButton.Enabled = false;
        _openOutputButton.Click += (_, _) => OpenOutputFolder();

        panel.Controls.Add(_openOutputButton);
        return panel;
    }

    void BrowseForFolder(TextBox target, string description)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
        {
            dialog.SelectedPath = Path.GetFullPath(target.Text);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    void RefreshOutputPreview()
    {
        string newFolder = _newFolderBox.Text.Trim();
        string fromVersion = _fromVersionBox.Text.Trim();
        string toVersion = _toVersionBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(newFolder)
            || !PatchValidation.IsValidVersion(fromVersion)
            || !PatchValidation.IsValidVersion(toVersion))
        {
            _outputPreviewLabel.Text = "Output: (choose folders and valid from/to versions)";
            return;
        }

        try
        {
            string zipPath = PatchValidation.GetZipPath(newFolder, fromVersion, toVersion);
            string indexPath = PatchValidation.GetUpdateIndexPath(newFolder);
            _outputPreviewLabel.Text = $"Output: {zipPath} + {Path.GetFileName(indexPath)}";
        }
        catch
        {
            _outputPreviewLabel.Text = "Output: (invalid new folder path)";
        }
    }

    async Task BuildPatchAsync()
    {
        var request = new PatchBuildRequest(
            _oldFolderBox.Text,
            _newFolderBox.Text,
            _fromVersionBox.Text,
            _toVersionBox.Text);

        string? error = PatchValidation.Validate(request);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Check your inputs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        IReadOnlyList<string> warnings = PatchValidation.GetWarnings(request);
        if (warnings.Count > 0)
        {
            var proceed = MessageBox.Show(
                this,
                string.Join("\n\n---\n\n", warnings) + "\n\nBuild anyway?",
                "This patch may strand players",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (proceed != DialogResult.Yes)
            {
                return;
            }
        }

        string zipPath = PatchValidation.GetZipPath(request.NewFolder, request.FromVersion, request.ToVersion);
        if (File.Exists(zipPath))
        {
            var overwrite = MessageBox.Show(
                this,
                $"This will replace an existing patch file:\n\n{zipPath}\n\nContinue?",
                "Replace existing patch?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (overwrite != DialogResult.Yes)
            {
                return;
            }
        }

        _buildCts = new CancellationTokenSource();
        SetBusy(true);
        _logBox.Clear();
        _lastZipPath = null;
        _openOutputButton.Enabled = false;

        var progress = new Progress<string>(AppendLog);

        try
        {
            AppendLog("Building patch...");
            PatchBuildResult result = await PatchBuildService.BuildAsync(
                request,
                progress,
                _buildCts.Token);

            _lastZipPath = result.ZipPath;
            _openOutputButton.Enabled = true;

            MessageBox.Show(
                this,
                $"Patch created successfully.\n\n" +
                $"Changed/new files: {result.ChangedOrNewFileCount:N0}\n" +
                $"Deleted files: {result.DeletedFileCount:N0}\n" +
                $"Size: {result.ZipSizeBytes / 1024.0 / 1024.0:F2} MB\n\n" +
                $"{result.ZipPath}\n" +
                $"{result.UpdateIndexPath}\n\n" +
                "Next: upload the .patch.zip and update-index.json to the GitHub release for that version.",
                "Patch ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Build cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(
                this,
                ex.Message,
                "Patch build failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            _buildCts.Dispose();
            _buildCts = null;
        }
    }

    void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_lastZipPath) || !File.Exists(_lastZipPath))
        {
            MessageBox.Show(
                this,
                "No patch zip is available yet. Build a patch first.",
                "Nothing to open",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_lastZipPath}\"",
                UseShellExecute = true
            });
    }

    void AppendLog(string message)
    {
        if (_logBox.TextLength > 0)
        {
            _logBox.AppendText(Environment.NewLine);
        }

        _logBox.AppendText(message);
    }

    void SetBusy(bool busy)
    {
        _buildButton.Enabled = !busy;
        _oldFolderBox.Enabled = !busy;
        _newFolderBox.Enabled = !busy;
        _fromVersionBox.Enabled = !busy;
        _toVersionBox.Enabled = !busy;
        _progressBar.Visible = busy;
        UseWaitCursor = busy;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_buildCts is not null)
        {
            var result = MessageBox.Show(
                this,
                "A patch build is still running. Cancel and close?",
                "Build in progress",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _buildCts.Cancel();
        }

        base.OnFormClosing(e);
    }
}
