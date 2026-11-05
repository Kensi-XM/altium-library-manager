using System.Text;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// STEP models the 3D viewer may download by key (https://models.viewer3d.local/{key}.step).
    /// Models are added from background threads when a footprint is prepared for display and served on the UI thread;
    /// the bytes are encoded up front so serving a request is just handing over a buffer.
    /// The viewer keeps the tessellated geometry itself, so a model is only needed here until it has been downloaded:
    /// the store is bounded by size, and a dropped model is added again whenever its footprint is shown.
    /// </summary>
    public class StepModelStore
    {
        private const int Capacity = 32;
        private const long MaxBytes = 64L << 20;

        private readonly object _sync = new();
        private readonly LruCache<string, byte[]> _models =
            new(Capacity, StringComparer.OrdinalIgnoreCase, data => data.Length, MaxBytes);

        public void Add(string key, string step)
        {
            lock (_sync)
            {
                if (_models.TryGet(key, out _))
                    return;
            }

            // STEP files are ASCII (ISO 10303-21), so Latin-1 maps one char to one byte without loss.
            // Encoded outside the lock: it takes a few milliseconds for large models
            var data = Encoding.Latin1.GetBytes(step);
            lock (_sync)
                _models.Add(key, data);
        }

        public bool TryGet(string key, out byte[] data)
        {
            lock (_sync)
                return _models.TryGet(key, out data);
        }
    }
}
