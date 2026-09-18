using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.PbpViewer {
    public partial class ViewerPane : UserControl, INotifyPropertyChanged {
        private const string SETTING_THEME_ID = "Theme";

        private PbpInfo _info;
        private ContextObject _context;
        private At3Player _player;
        private PmfPlayer _pmfPlayer;

        public event PropertyChangedEventHandler PropertyChanged;

        public PbpInfo Info {
            get => _info;
            set {
                _info = value;
                UpdateUI();
            }
        }

        public Themes Theme {
            get => _context?.Theme ?? Themes.Dark;
            set {
                if (_context == null) return;
                _context.Theme = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDark));
            }
        }

        public bool IsDark => Theme == Themes.Dark;

        public ViewerPane() {
            InitializeComponent();
            btnSwTheme.MouseLeftButtonDown += SwitchTheme;
            btnPlaySnd0.MouseLeftButtonDown += ToggleSnd0;
            Unloaded += OnUnloaded;
        }

        public ViewerPane(ContextObject context) : this() {
            _context = context;
            _context.PropertyChanged += AfterThemeChanged;
            Theme = (Themes) SettingHelper.Get(SETTING_THEME_ID, 1, GetType().Namespace);
        }

        static bool HasData(string s) =>
            !string.IsNullOrWhiteSpace(s) && s != "—";

        void SetRow(TextBlock label, TextBox box, string value) {
            if (HasData(value)) {
                box.Text = value;
                label.Visibility = Visibility.Visible;
                box.Visibility = Visibility.Visible;
            }
            else {
                box.Text = "";
                label.Visibility = Visibility.Collapsed;
                box.Visibility = Visibility.Collapsed;
            }
        }

        private void SwitchTheme(object sender, MouseButtonEventArgs e) {
            Theme = IsDark ? Themes.Light : Themes.Dark;
            SettingHelper.Set(SETTING_THEME_ID, (int) Theme, GetType().Namespace);
        }

        private void ToggleSnd0(object sender, MouseButtonEventArgs e) {
            if (_player == null) return;

            _player.Toggle();

            bool playing = _player.IsPlaying;

            btnPlaySnd0.Source = new BitmapImage(
                new Uri(
                    playing ? "images/snd_on.png" : "images/snd_off.png",
                    UriKind.Relative));

            btnPlaySnd0.ToolTip = playing
                ? "Stop SND0"
                : "Play SND0 (loop)";
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            _player?.Dispose();
            _player = null;

            _pmfPlayer?.Dispose();
            _pmfPlayer = null;
        }

        private void AfterThemeChanged(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName != nameof(ContextObject.Theme) && e.PropertyName != nameof(Theme))
                return;

            var resourceUri = "/QuickLook.Common;component/Styles" +
                              $"/MainWindowStyles{(IsDark ? ".Dark" : "")}.xaml";

            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(new ResourceDictionary {
                Source = new Uri(resourceUri, UriKind.Relative)
            });
        }

        private void UpdateUI() {
            if (_info == null) return;

            SetRow(lblTitle, tbTitle, _info.Title);
            SetRow(lblTitleId, tbTitleId, _info.TitleId);
            SetRow(lblAppVer, tbAppVer, _info.AppVer);
            SetRow(lblFirmware, tbFirmware, _info.PspSystemVer);
            SetRow(lblCategory, tbCategory, _info.Category);
            SetRow(lblDiscId, tbDiscId, _info.DiscId);
            SetRow(lblDiscVer, tbDiscVer, _info.DiscVersion);
            SetRow(lblParental, tbParental, _info.ParentalLevel);
            SetRow(lblRegion, tbRegion, _info.Region);
            SetRow(lblBootable, tbBootable, _info.Bootable);
            SetRow(lblSize, tbSize, _info.FileSize);

            SetRow(lblIcon1, tbIcon1, _info.HasIcon1 ? "Yes" : null);
            SetRow(lblSnd0, tbSnd0, _info.HasSnd0 ? "Yes" : null);

            SetRow(lblDemo, tbDemo, _info.IsDemo);
            SetRow(lblFakeNp, tbFakeNp, _info.IsFakeNp);

            imgIcon0.Source = _info.Icon0 ?? (Resources["DefaultIcon"] as BitmapImage);

            if (_info.Pic0 != null) {
                imgPic0.Source = _info.Pic0;
                borderPic0.Visibility = Visibility.Visible;
                lblPic0.Visibility = Visibility.Visible;
            }
            else {
                borderPic0.Visibility = Visibility.Collapsed;
                lblPic0.Visibility = Visibility.Collapsed;
            }

            if (_info.Pic1 != null) {
                imgPic1.Source = _info.Pic1;
                borderPic1.Visibility = Visibility.Visible;
                lblPic1.Visibility = Visibility.Visible;
            }
            else {
                borderPic1.Visibility = Visibility.Collapsed;
                lblPic1.Visibility = Visibility.Collapsed;
            }

            if (_info.Boot != null) {
                imgBoot.Source = _info.Boot;
                borderBoot.Visibility = Visibility.Visible;
                lblBoot.Visibility = Visibility.Visible;
            }
            else {
                borderBoot.Visibility = Visibility.Collapsed;
                lblBoot.Visibility = Visibility.Collapsed;
            }

            // SND0 player — по умолчанию выключен
            _player?.Dispose();
            _player = null;
            btnPlaySnd0.Source = new BitmapImage(
                new Uri("images/snd_off.png", UriKind.Relative));

            btnPlaySnd0.ToolTip = "Play SND0 (loop)";

            if (_info.Snd0Data != null && _info.Snd0Data.Length > 0) {
                _player = new At3Player();
                _player.SetData(_info.Snd0Data);
            }


            // Показывать иконку, если секция есть ИЛИ данные загружены
            bool show = _info.HasSnd0 || (_info.Snd0Data != null && _info.Snd0Data.Length > 0);
            btnPlaySnd0.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // ===== ICON1.PMF =====
            _pmfPlayer?.Dispose();
            _pmfPlayer = null;
            panelIcon1.Visibility = Visibility.Collapsed; // Показываем/скрываем всю панель с текстом
            imgIcon1.Source = null;

            if (_info.Icon1Data != null && _info.Icon1Data.Length > 2048) {
                _pmfPlayer = new PmfPlayer();
                _pmfPlayer.SetData(_info.Icon1Data);

                _pmfPlayer.FrameUpdated += () =>
                {
                    imgIcon1.Source = _pmfPlayer.CurrentFrame;
                };

                panelIcon1.Visibility = Visibility.Visible;
                _pmfPlayer.Start(Dispatcher);
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}