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

            //clean up old archives on each write to ensure we don't keep old files around indefinitely
             CleanupOldArchives();

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
        }

        public void CleanupOldArchives()
        {
            var directory = Path.GetDirectoryName(_filepath);

            var files = Directory.GetFiles(directory, "captures-*.jsonl");

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
