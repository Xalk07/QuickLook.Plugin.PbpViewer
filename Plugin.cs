using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.PbpViewer;

namespace QuickLook.Plugin.PbpViewer {
    public class Plugin : IViewer {
        public int Priority => 0;

        public void Init() { }

        public bool CanHandle(string path) {
            if (string.IsNullOrEmpty(path) || Directory.Exists(path))
                return false;

            var name = Path.GetFileName(path).ToLowerInvariant();
            return name.EndsWith(".pbp"); // EBOOT.PBP, PBOOT.PBP, PARAM.PBP и любые *.pbp
        }

        public void Prepare(string path, ContextObject context) {
            context.PreferredSize = new Size(700, 485);
            context.Title = Path.GetFileName(path);
        }

        public void View(string path, ContextObject context) {
            try {
                var info = PbpParser.Parse(path);
                context.ViewerContent = new ViewerPane(context) { Info = info };
            }
            catch (Exception ex) {
                context.ViewerContent = new Label {
                    Content = "Не удалось прочитать PBP\n" + ex.Message,
                    FontSize = 14,
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
            }

            context.IsBusy = false;
        }

        public void Cleanup() { }
    }
}