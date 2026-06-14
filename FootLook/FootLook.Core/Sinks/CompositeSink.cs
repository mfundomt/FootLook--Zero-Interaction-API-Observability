using FootLook.Core.Interfaces;
using FootLook.Core.Models;

namespace FootLook.Core.Sinks
{
    public class CompositeSink : IShadowSink
    {
        private readonly IEnumerable<IShadowSink> _sinks;
        public CompositeSink(IEnumerable<IShadowSink> sinks)
        {
            _sinks = sinks; 
        }

        public async Task WriteAsync(CapturedRequest request)
        {
            foreach(var sink in _sinks)
            {
                try
                {
                    await sink.WriteAsync(request);
                }
                catch (Exception ex)
                {
                    // Log the error but continue with the next sink
                    Console.Error.WriteLine($"FootLook sink failed: {sink.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
