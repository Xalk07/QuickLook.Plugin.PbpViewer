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
        private const string SETTING_BG_ID = "BackgroundMode";   // 0 = none, 1 = pic1, 2 = pmf

        private PbpInfo _info;
        private ContextObject _context;
        private At3Player _player;
        private PmfPlayer _pmfPlayer;
        private bool _bgPic1Enabled;
        private bool _bgPmfEnabled;
        private PmfPlayer _bgPmfPlayer;   // отдельный экземпляр для фона


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

            // ← вот сюда
            btnBgPic1.MouseLeftButtonDown += ToggleBgPic1;
            btnBgPmf.MouseLeftButtonDown += ToggleBgPmf;

            Unloaded += OnUnloaded;
        }

        public ViewerPane(ContextObject context) : this() {
            _context = context;
            _context.PropertyChanged += AfterThemeChanged;
            Theme = (Themes) SettingHelper.Get(SETTING_THEME_ID, 1, GetType().Namespace);
        }

        static bool HasData(string s) =>
            !string.IsNullOrWhiteSpace(s) && s != "—";

        private void ToggleBgPic1(object sender, MouseButtonEventArgs e) {
            _bgPic1Enabled = !_bgPic1Enabled;
            _bgPmfEnabled = false;
            StopBgVideo();
            ApplyBackground();
            UpdateBgButtons();

            // сохраняем
            int mode = _bgPic1Enabled ? 1 : 0;
            SettingHelper.Set(SETTING_BG_ID, mode, GetType().Namespace);
        }

        private void ToggleBgPmf(object sender, MouseButtonEventArgs e) {
            _bgPmfEnabled = !_bgPmfEnabled;
            _bgPic1Enabled = false;

            if (_bgPmfEnabled)
                StartBgVideo();
            else
                StopBgVideo();

            ApplyBackground();
            UpdateBgButtons();

            // сохраняем
            int mode = _bgPmfEnabled ? 2 : 0;
            SettingHelper.Set(SETTING_BG_ID, mode, GetType().Namespace);
        }

        private void ApplyBackground() {
            if (_bgPic1Enabled && _info?.Pic1 != null) {
                bgImage.Source = _info.Pic1;
                bgImage.Visibility = Visibility.Visible;
                bgVideo.Visibility = Visibility.Collapsed;
            }
            else if (_bgPmfEnabled) {
                bgImage.Visibility = Visibility.Collapsed;
                bgVideo.Visibility = Visibility.Visible;
            }
            else {
                bgImage.Visibility = Visibility.Collapsed;
                bgVideo.Visibility = Visibility.Collapsed;
                bgImage.Source = null;
                bgVideo.Source = null;
            }
        }

        private void UpdateBgButtons() {
            // PIC1
            if (btnBgPic1 != null) {
                btnBgPic1.Source = new BitmapImage(new Uri(
                    _bgPic1Enabled ? "images/bg_pic_on.png" : "images/bg_pic_off.png",
                    UriKind.Relative));
                btnBgPic1.ToolTip = _bgPic1Enabled ? "Disable PIC1 background" : "Enable PIC1 background";
            }

            // PMF
            if (btnBgPmf != null) {
                btnBgPmf.Source = new BitmapImage(new Uri(
                    _bgPmfEnabled ? "images/bg_pmf_on.png" : "images/bg_pmf_off.png",
                    UriKind.Relative));
                btnBgPmf.ToolTip = _bgPmfEnabled ? "Disable PMF background" : "Enable PMF background";
            }
        }

        private void StartBgVideo() {
            StopBgVideo();

            if (_info?.Icon1Data == null || _info.Icon1Data.Length <= 2048)
                return;

            _bgPmfPlayer = new PmfPlayer();
            _bgPmfPlayer.SetData(_info.Icon1Data);

            _bgPmfPlayer.FrameUpdated += () =>
            {
                bgVideo.Source = _bgPmfPlayer.CurrentFrame;
            };

            _bgPmfPlayer.Start(Dispatcher);
        }

        private void StopBgVideo() {
            _bgPmfPlayer?.Dispose();
            _bgPmfPlayer = null;
            if (bgVideo != null)
                bgVideo.Source = null;
        }

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

            _bgPmfPlayer?.Dispose();
            _bgPmfPlayer = null;
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

            SetRow(lblIcon1, tbIcon1, _info.HasIcon1 ? (_info.Icon1Size ??  "Yes") : null);
            SetRow(lblSnd0, tbSnd0, _info.HasSnd0 ? (_info.Snd0Size ?? "Yes") : null);

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
            
            // ===== Восстановление фона из настроек =====
            int savedMode = SettingHelper.Get(SETTING_BG_ID, 0, GetType().Namespace);

            bool hasPic1 = _info.Pic1 != null;
            bool hasPmf = _info.Icon1Data != null && _info.Icon1Data.Length > 2048;

            _bgPic1Enabled = false;
            _bgPmfEnabled = false;
            StopBgVideo();

            if (savedMode == 2 && hasPmf)          // пользователь любит видео
            {
                _bgPmfEnabled = true;
                StartBgVideo();
            }
            else if (savedMode == 1 && hasPic1)    // пользователь любит картинку
            {
                _bgPic1Enabled = true;
            }
            else if (savedMode == 2 && hasPic1)    // хотел видео, но его нет → берём картинку
            {
                _bgPic1Enabled = true;
            }
            // иначе оставляем выключенным (дефолт)

            ApplyBackground();
            UpdateBgButtons();

            // Показывать кнопки только если есть данные
            btnBgPic1.Visibility = _info.Pic1 != null ? Visibility.Visible : Visibility.Collapsed;
            btnBgPmf.Visibility = (_info.Icon1Data != null && _info.Icon1Data.Length > 2048)
                                  ? Visibility.Visible : Visibility.Collapsed;
        }


        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}