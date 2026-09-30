using System.Windows.Forms;

namespace CouchCompanion.Models
{
    public class ScreenInfo
    {
        public Screen Screen { get; }
        public string DeviceName => Screen.DeviceName;
        public int BoundsX => Screen.Bounds.X;
        public int BoundsY => Screen.Bounds.Y;
        public int BoundsWidth => Screen.Bounds.Width;
        public int BoundsHeight => Screen.Bounds.Height;

        public ScreenInfo(Screen screen)
        {
            Screen = screen;
        }
    }
}
