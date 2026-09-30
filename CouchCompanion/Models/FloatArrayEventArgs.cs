using System;

namespace CouchCompanion.Models
{
    public class FloatArrayEventArgs : EventArgs
    {
        public float[] Buffer { get; }
        public int BytesRecorded { get; }

        public FloatArrayEventArgs(float[] buffer, int bytesRecorded)
        {
            Buffer = buffer;
            BytesRecorded = bytesRecorded;
        }
    }
}
