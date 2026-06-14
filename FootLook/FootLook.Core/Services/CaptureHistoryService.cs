using FootLook.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FootLook.Core.Services
{
    public class CaptureHistoryService
    {
        private readonly string _filePath;

        public CaptureHistoryService()
        {
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures.jsonl");
        }

        public IEnumerable<CapturedRequest> GetRecent(int count)
        {
            if (!File.Exists(_filePath))
            {
                return Enumerable.Empty<CapturedRequest>();
            }

            return File.ReadLines(_filePath)
                .Reverse()
                .Take(count)
                .Select(line =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<CapturedRequest>(line);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(x => x is not null)!
                .Cast<CapturedRequest>();
        }
    }
}
