using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;

namespace RvtMcp.Plugin.Chat
{
    /// <summary>Dockable "Chat IA" pane: Claude (Anthropic) or OpenAI/Codex models, tool-use over rvt-mcp.</summary>
    public sealed class ChatPane : UserControl, IDockablePaneProvider, IChatHost
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("7b1f3c52-2d4e-4f7a-9a1b-5c6d7e8f9a01"));
        public static ChatPane Instance { get; private set; }

        private readonly StackPanel _log = new StackPanel { Margin = new Thickness(4) };
        private readonly ScrollViewer _scroll;
        private readonly TextBox _input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly ComboBox _provider = new ComboBox { Width = 120 };
        private readonly ComboBox _model = new ComboBox { IsEditable = true, MinWidth = 160 };
        private readonly Button _send = new Button { Content = "Envoyer", Padding = new Thickness(10, 2, 10, 2) };
        private readonly Button _stop = new Button { Content = "Stop", IsEnabled = false, Margin = new Thickness(4, 0, 0, 0) };
        private readonly TextBlock _status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(4, 2, 4, 2) };
        private readonly McpStdioClient _mcp = new McpStdioClient();
        private readonly ChatAgent _agent;
        private CancellationTokenSource _cts;

        private static readonly string[] ClaudeModels = { "claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5" };
        private static readonly string[] OpenAiModels = { "gpt-5", "gpt-5-codex", "gpt-5-mini", "gpt-4.1" };

        public ChatPane()
        {
            Instance = this;
            _agent = new ChatAgent(_mcp, this);
            Background = Brushes.White;

            var top = new DockPanel { Margin = new Thickness(4) };
            _provider.Items.Add("Claude");
            _provider.Items.Add("OpenAI / Codex");
            _provider.SelectedIndex = 0;
            _provider.SelectionChanged += (s, e) => FillModels();
            var keyBtn = new Button { Content = "Clé API…", Margin = new Thickness(4, 0, 0, 0) };
            keyBtn.Click += (s, e) => EditKey();
            var clearBtn = new Button { Content = "Nouveau", Margin = new Thickness(4, 0, 0, 0) };
            clearBtn.Click += (s, e) => { _agent.Reset(); _log.Children.Clear(); AddBubble("Nouvelle conversation.", Brushes.Gray); };
            var row = new WrapPanel();
            row.Children.Add(_provider);
            row.Children.Add(new TextBlock { Text = " ", Width = 4 });
            row.Children.Add(_model);
            row.Children.Add(keyBtn);
            row.Children.Add(clearBtn);
            top.Children.Add(row);

            _scroll = new ScrollViewer { Content = _log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var bottom = new DockPanel { Margin = new Thickness(4) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 0) };
            buttons.Children.Add(_send);
            buttons.Children.Add(_stop);
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            bottom.Children.Add(_input);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(top, 0); Grid.SetRow(_scroll, 1); Grid.SetRow(_status, 2); Grid.SetRow(bottom, 3);
            grid.Children.Add(top); grid.Children.Add(_scroll); grid.Children.Add(_status); grid.Children.Add(bottom);
            Content = grid;

            _send.Click += async (s, e) => await SendAsync();
            _stop.Click += (s, e) => _cts?.Cancel();
            _input.PreviewKeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await SendAsync(); }
            };
            FillModels();
            AddBubble("Bonjour ! Posez une question sur la maquette ouverte. Les modifications vous seront toujours soumises pour validation.\n" +
                      "Hello! Ask about the open model. Every change asks for your approval first.", Brushes.Gray);
        }

        private string ProviderKey { get { return _provider.SelectedIndex == 1 ? "openai" : "anthropic"; } }

        private void FillModels()
        {
            _model.Items.Clear();
            foreach (var m in ProviderKey == "openai" ? OpenAiModels : ClaudeModels) _model.Items.Add(m);
            _model.SelectedIndex = 0;
            _status.Text = ChatSecretStore.Has(ProviderKey) ? "Clé API configurée (chiffrée DPAPI)." : "Aucune clé API : cliquez « Clé API… ».";
        }

        private void EditKey()
        {
            var name = ProviderKey == "openai" ? "OpenAI" : "Anthropic";
            var box = new PasswordBox { Margin = new Thickness(8) };
            var ok = new Button { Content = "Enregistrer", IsDefault = true, Margin = new Thickness(8), Padding = new Thickness(10, 2, 10, 2) };
            var del = new Button { Content = "Supprimer la clé", Margin = new Thickness(8) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Clé API " + name + " (stockée chiffrée avec Windows DPAPI, jamais en clair) :", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(box);
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bp.Children.Add(del); bp.Children.Add(ok);
            panel.Children.Add(bp);
            var w = new Window { Title = "rvt-mcp – " + name, Content = panel, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
            ok.Click += (s, e) => { ChatSecretStore.Save(ProviderKey, box.Password); w.DialogResult = true; };
            del.Click += (s, e) => { ChatSecretStore.Delete(ProviderKey); w.DialogResult = true; };
            w.ShowDialog();
            FillModels();
        }

        private async Task SendAsync()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0 || !_send.IsEnabled) return;
            var key = ChatSecretStore.Load(ProviderKey);
            if (string.IsNullOrEmpty(key)) { EditKey(); key = ChatSecretStore.Load(ProviderKey); if (string.IsNullOrEmpty(key)) return; }

            _input.Clear();
            AddBubble(text, Brushes.Black, true);
            _agent.Provider = ProviderKey;
            _agent.Model = (_model.Text ?? "").Trim();
            _send.IsEnabled = false; _stop.IsEnabled = true;
            _cts = new CancellationTokenSource();
            try
            {
                var answer = await _agent.SendAsync(text, key, _cts.Token);
                AddBubble(answer, Brushes.Black);
                _status.Text = "";
            }
            catch (OperationCanceledException) { AddBubble("Annulé.", Brushes.Gray); _status.Text = ""; }
            catch (Exception ex) { AddBubble("Erreur : " + ex.Message, Brushes.DarkRed); _status.Text = ""; }
            finally { _send.IsEnabled = true; _stop.IsEnabled = false; _cts.Dispose(); _cts = null; }
        }

        private void AddBubble(string text, Brush fg, bool user = false)
        {
            Ui(() =>
            {
                var tb = new TextBox
                {
                    Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap,
                    Foreground = fg, Background = user ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)) : Brushes.Transparent,
                    Padding = new Thickness(6), Margin = new Thickness(user ? 24 : 0, 3, user ? 0 : 24, 3)
                };
                _log.Children.Add(tb);
                _scroll.ScrollToEnd();
            });
        }

        private void Ui(Action a)
        {
            if (Dispatcher.CheckAccess()) a(); else Dispatcher.Invoke(a);
        }

        // ---- IChatHost
        public void OnStatus(string text) { Ui(() => _status.Text = text); }

        public void OnToolCall(string tool, string argsJson, ToolRisk risk)
        {
            AddBubble("→ " + tool + "  [" + RiskLabel(risk) + "]", Brushes.SteelBlue);
        }

        public void OnToolResult(string tool, string result, bool isError)
        {
            var shortText = result.Length > 600 ? result.Substring(0, 600) + " …" : result;
            AddBubble((isError ? "✗ " : "✓ ") + tool + "\n" + shortText, isError ? Brushes.DarkRed : Brushes.DarkGreen);
        }

        public Task<bool> ConfirmAsync(string tool, string description, string argsJson, ToolRisk risk)
        {
            var tcs = new TaskCompletionSource<bool>();
            Ui(() =>
            {
                var panel = new StackPanel { Margin = new Thickness(10) };
                panel.Children.Add(new TextBlock
                {
                    Text = "L'IA veut exécuter / The AI wants to run:\n" + tool + "   — niveau de risque : " + RiskLabel(risk),
                    FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap
                });
                if (risk == ToolRisk.Destructive || risk == ToolRisk.Code)
                    panel.Children.Add(new TextBlock { Text = "⚠ Action destructive ou code arbitraire. Vérifiez attentivement. Annulable avec Ctrl+Z dans Revit.", Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
                panel.Children.Add(new TextBlock { Text = description.Length > 400 ? description.Substring(0, 400) + "…" : description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 6) });
                panel.Children.Add(new TextBox { Text = argsJson, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
                var always = new CheckBox { Content = "Toujours autoriser cet outil pendant cette session", Margin = new Thickness(0, 8, 0, 0), IsEnabled = risk == ToolRisk.Write };
                panel.Children.Add(always);
                var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var yes = new Button { Content = "Autoriser", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(4) };
                var no = new Button { Content = "Refuser", IsDefault = true, IsCancel = true, Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(4) };
                bp.Children.Add(no); bp.Children.Add(yes);
                panel.Children.Add(bp);
                var w = new Window { Title = "rvt-mcp – confirmation", Content = panel, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
                yes.Click += (s, e) => { if (always.IsChecked == true) _agent.AllowForSession(tool); w.DialogResult = true; };
                var result = w.ShowDialog() == true;
                tcs.TrySetResult(result);
            });
            return tcs.Task;
        }

        private static string RiskLabel(ToolRisk r)
        {
            switch (r)
            {
                case ToolRisk.Read: return "lecture";
                case ToolRisk.Write: return "écriture";
                case ToolRisk.Destructive: return "DESTRUCTIF";
                default: return "CODE C#";
            }
        }

        // ---- IDockablePaneProvider
        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
            data.VisibleByDefault = false;
        }

        public static void Register(UIControlledApplication app)
        {
            try { app.RegisterDockablePane(PaneId, "Chat IA (rvt-mcp)", new ChatPane()); }
            catch (Exception ex) { McpLoggerSafe("Chat pane registration failed: " + ex.Message); }
        }

        private static void McpLoggerSafe(string msg) { try { System.Diagnostics.Debug.WriteLine(msg); } catch { } }
    }
}
