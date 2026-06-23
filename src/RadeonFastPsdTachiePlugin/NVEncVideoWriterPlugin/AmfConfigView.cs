using System;
using System.Windows;
using System.Windows.Controls;

namespace RadeonAmfVideoWriterPlugin;

internal sealed class AmfConfigView : UserControl
{
    private readonly ComboBox _codecComboBox;
    private readonly ComboBox _rateControlComboBox;
    private readonly TextBox _bitrateTextBox;
    private readonly ComboBox _qualityComboBox;
    private readonly TextBox _queueDepthTextBox;
    private readonly CheckBox _preAnalysisCheckBox;
    private readonly CheckBox _debugLogCheckBox;
    private readonly AmfSettings _settings;

    public AmfConfigView(AmfSettings settings)
    {
        _settings = settings;
        var panel = new StackPanel
        {
            Margin = new Thickness(8),
        };

        panel.Children.Add(new TextBlock
        {
            Text = "コーデック",
            Margin = new Thickness(0, 0, 0, 4),
        });

        _codecComboBox = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = new[] { "H.264 (AMF)", "H.265 / HEVC (AMF)" },
            SelectedIndex = _settings.Codec == AmfCodec.H265 ? 1 : 0,
        };
        _codecComboBox.SelectionChanged += (_, _) =>
        {
            _settings.Codec = _codecComboBox.SelectedIndex == 1 ? AmfCodec.H265 : AmfCodec.H264;
        };
        panel.Children.Add(_codecComboBox);

        panel.Children.Add(new TextBlock
        {
            Text = "ビットレート方式",
            Margin = new Thickness(0, 0, 0, 4),
        });

        _rateControlComboBox = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = new[] { "固定 (CBR)", "可変 (AMF VBR)", "自動 (YouTube 推奨)" },
            SelectedIndex = _settings.RateControl switch
            {
                AmfRateControl.Variable => 1,
                AmfRateControl.YouTubeRecommended => 2,
                _ => 0,
            },
        };
        panel.Children.Add(_rateControlComboBox);

        panel.Children.Add(new TextBlock
        {
            Text = "出力品質",
            Margin = new Thickness(0, 0, 0, 4),
        });

        _qualityComboBox = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = new[] { "高速", "標準", "高品質" },
            SelectedIndex = (int)_settings.Quality,
        };
        _qualityComboBox.SelectionChanged += (_, _) =>
        {
            _settings.Quality = (AmfQuality)Math.Clamp(_qualityComboBox.SelectedIndex, 0, 2);
            if (_settings.Quality == AmfQuality.Speed)
            {
                _settings.EnablePreAnalysis = false;
                _settings.QueueDepth = 32;
                _preAnalysisCheckBox.IsChecked = false;
                _queueDepthTextBox.Text = _settings.QueueDepth.ToString();
            }
            if (_settings.Quality == AmfQuality.Quality)
            {
                _settings.EnablePreAnalysis = true;
                _preAnalysisCheckBox.IsChecked = true;
            }
        };
        panel.Children.Add(_qualityComboBox);

        panel.Children.Add(new TextBlock
        {
            Text = "ビットレート（kbps）",
            Margin = new Thickness(0, 0, 0, 4),
        });

        _bitrateTextBox = new TextBox
        {
            Text = _settings.BitrateKbps.ToString(),
            Margin = new Thickness(0, 0, 0, 12),
            IsEnabled = _settings.RateControl != AmfRateControl.YouTubeRecommended,
        };
        _rateControlComboBox.SelectionChanged += (_, _) =>
        {
            _settings.RateControl = _rateControlComboBox.SelectedIndex switch
            {
                1 => AmfRateControl.Variable,
                2 => AmfRateControl.YouTubeRecommended,
                _ => AmfRateControl.Fixed,
            };

            _bitrateTextBox.IsEnabled = _settings.RateControl != AmfRateControl.YouTubeRecommended;
            if (_settings.RateControl != AmfRateControl.YouTubeRecommended)
            {
                _settings.BitrateKbps = Math.Clamp(_settings.BitrateKbps, 100, 200000);
                _bitrateTextBox.Text = _settings.BitrateKbps.ToString();
            }
        };
        _bitrateTextBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(_bitrateTextBox.Text, out var value))
            {
                _settings.BitrateKbps = Math.Clamp(value, 100, 200000);
            }
        };
        panel.Children.Add(_bitrateTextBox);

        panel.Children.Add(new TextBlock
        {
            Text = "GPUキュー深度",
            Margin = new Thickness(0, 0, 0, 4),
        });

        _queueDepthTextBox = new TextBox
        {
            Text = _settings.QueueDepth.ToString(),
            Margin = new Thickness(0, 0, 0, 12),
        };
        _queueDepthTextBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(_queueDepthTextBox.Text, out var value))
            {
                _settings.QueueDepth = Math.Clamp(value, 4, 32);
            }
        };
        panel.Children.Add(_queueDepthTextBox);

        _preAnalysisCheckBox = new CheckBox
        {
            Content = "PreAnalysis / 高品質解析を使う",
            IsChecked = _settings.EnablePreAnalysis,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _preAnalysisCheckBox.Checked += (_, _) => _settings.EnablePreAnalysis = true;
        _preAnalysisCheckBox.Unchecked += (_, _) => _settings.EnablePreAnalysis = false;
        panel.Children.Add(_preAnalysisCheckBox);

        _debugLogCheckBox = new CheckBox
        {
            Content = "デバッグログを書き出す",
            IsChecked = _settings.EnableDebugLog,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _debugLogCheckBox.Checked += (_, _) => _settings.EnableDebugLog = true;
        _debugLogCheckBox.Unchecked += (_, _) => _settings.EnableDebugLog = false;
        panel.Children.Add(_debugLogCheckBox);

        Content = panel;
    }
}
