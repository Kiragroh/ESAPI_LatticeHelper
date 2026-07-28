using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LatticeGenerator;

internal static class LatticeDialogHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3 || args.Length > 5)
        {
            Console.Error.WriteLine(
                "Usage: LatticeDialogHarness <png-path> <width> <height> [scroll-offset] [layer-output]");
            return 2;
        }

        string outputPath = Path.GetFullPath(args[0]);
        double width = double.Parse(args[1]);
        double height = double.Parse(args[2]);
        double scrollOffset = args.Length >= 4 ? double.Parse(args[3]) : 0.0;
        bool layerOutput = args.Length >= 5 && bool.Parse(args[4]);

        var targets = new List<LatticeStructureOption>
        {
            new LatticeStructureOption { Id = "GTV_Bulky_Primary", VolumeCc = 183.4 },
            new LatticeStructureOption { Id = "GTV_Node", VolumeCc = 24.8 }
        };
        var structures = new List<LatticeStructureOption>
        {
            new LatticeStructureOption { Id = "GTV_Bulky_Primary", VolumeCc = 183.4 },
            new LatticeStructureOption { Id = "GTV_Node", VolumeCc = 24.8 },
            new LatticeStructureOption { Id = "Chestwall_R", VolumeCc = 422.1 },
            new LatticeStructureOption { Id = "Bronchus_Main", VolumeCc = 19.3 },
            new LatticeStructureOption { Id = "Heart", VolumeCc = 612.5 },
            new LatticeStructureOption { Id = "Lung_L", VolumeCc = 1460.2 },
            new LatticeStructureOption { Id = "SpinalCord", VolumeCc = 52.4 }
        };

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        var dialog = new LatticeDialog(targets, structures)
        {
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000.0,
            Top = 0.0,
            ShowInTaskbar = false
        };

        dialog.Show();
        dialog.Dispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            new Action(delegate { }));
        if (layerOutput)
        {
            CheckBox manualCheckBox = FindVisualChildren<CheckBox>(dialog).First();
            manualCheckBox.IsChecked = true;
        }

        ScrollViewer formScrollViewer = FindVisualChildren<ScrollViewer>(dialog)
            .FirstOrDefault(item => item.Content is StackPanel);
        if (formScrollViewer != null)
        {
            formScrollViewer.ScrollToVerticalOffset(scrollOffset);
        }

        dialog.Dispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            new Action(delegate { }));
        dialog.UpdateLayout();

        Matrix transform = PresentationSource.FromVisual(dialog)
            .CompositionTarget
            .TransformToDevice;
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(dialog.ActualWidth * transform.M11));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(dialog.ActualHeight * transform.M22));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96.0 * transform.M11,
            96.0 * transform.M22,
            PixelFormats.Pbgra32);
        bitmap.Render(dialog);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(outputPath))
        {
            encoder.Save(stream);
        }

        dialog.Close();
        application.Shutdown();
        Console.WriteLine(outputPath);
        return 0;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            T match = child as T;
            if (match != null)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
