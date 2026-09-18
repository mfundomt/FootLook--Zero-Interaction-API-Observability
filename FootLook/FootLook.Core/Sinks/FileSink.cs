using FootLook.Core.Interfaces;
using System.Text.Json;
using FootLook.Core.Models;
using FootLook.Core.Options;

namespace FootLook.Core.Sinks
{
    public class FileSink : IShadowSink
    {
        private readonly string _filepath;
        private readonly FootLookOptions _options;
        public FileSink(FootLookOptions options)
        {
            _options = options;
            _filepath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures.jsonl");
        }

        public async Task WriteAsync(CapturedRequest request)
        {
            //check file size before each write and rotate if necessary to avoid unbounded file growth
            RotateFileIfItExceedsLimit();

            var json = JsonSerializer.Serialize(request);
            await File.AppendAllTextAsync(_filepath, json + Environment.NewLine);
        }

        private void RotateFileIfItExceedsLimit()
        {
            if(!File.Exists(_filepath))
            {
                return;
            }

            var fileInformation = new FileInfo(_filepath);
            if(fileInformation.Length < _options.MaxFileSizeBytes)
            {
                return;
            }

            var archivePath = Path.Combine(Path.GetDirectoryName(_filepath)!,
                                          $"{Path.GetFileNameWithoutExtension(_filepath)}_{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl");
            File.Move(_filepath, archivePath);

            // Only worth scanning the directory for expired archives right after we just
            // created one - rotation is rare (MaxFileSizeBytes is 100MB by default), so this
            // keeps the directory scan off the per-write hot path entirely instead of
            // running it (and its full Directory.GetFiles + per-file stat cost) on every
            // single capture.
            CleanupOldArchives();
        }

        public void CleanupOldArchives()
        {
            var directory = Path.GetDirectoryName(_filepath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var files = Directory.GetFiles(directory, "captures_*.jsonl");

            foreach( var file in files)
            {
                var age = DateTime.UtcNow - File.GetCreationTimeUtc(file);
                if (age.TotalDays > _options.RetentionDays)
                {
                    File.Delete(file);

                }
            }
        }


    }
}
