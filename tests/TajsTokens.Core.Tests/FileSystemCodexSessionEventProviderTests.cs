using System.Text;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Core.Tests;

public sealed class FileSystemCodexSessionEventProviderTests
{
    [Fact]
    public async Task ChunkedReaderPreservesUtf8ByteOffsetsCrLfAndIncompleteTail()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-jsonl-").FullName;
        string path = Path.Combine(directory, "rollout.jsonl");
        try
        {
            // Split both a UTF-8 character and a newline across 64 KiB input boundaries.
            string first = new('a', 65535);
            first += "ą" + new string('b', 65533);
            string tail = "{\"tail\":\"ę\"}";
            string complete = first + "\r\n \n{\"n\":2}\n";
            await File.WriteAllTextAsync(path, complete + tail, new UTF8Encoding(false));
            var provider = new FileSystemCodexSessionEventProvider();
            var records = new List<RawSessionRecord>();
            await foreach (RawSessionRecord record in provider.ReadNewJsonLinesAsync(path, 0, CancellationToken.None))
                records.Add(record);
            Assert.Equal(2, records.Count);
            Assert.Equal(first, records[0].Payload);
            Assert.Equal(0, records[0].StartByteOffset);
            Assert.Equal(Encoding.UTF8.GetByteCount(first + "\r\n"), records[0].EndByteOffset);
            Assert.Equal(Encoding.UTF8.GetByteCount(first + "\r\n \n"), records[1].StartByteOffset);
            long offset = Encoding.UTF8.GetByteCount(complete);
            Assert.Equal(offset, records[1].EndByteOffset);

            await File.AppendAllTextAsync(path, "\n");
            records.Clear();
            await foreach (RawSessionRecord record in provider.ReadNewJsonLinesAsync(path, offset, CancellationToken.None))
                records.Add(record);
            Assert.Equal(tail, Assert.Single(records).Payload);
            Assert.Equal(offset, records[0].StartByteOffset);
            Assert.Equal(new FileInfo(path).Length, records[0].EndByteOffset);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LargeRecordFollowedBySmallRecordsRetainsEveryPayload()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-jsonl-").FullName;
        string path = Path.Combine(directory, "rollout.jsonl");
        try
        {
            string large = new('x', 2 * 1024 * 1024);
            await File.WriteAllTextAsync(path, large + "\n{}\n[]\n");
            var payloads = new List<string>();
            await foreach (RawSessionRecord record in new FileSystemCodexSessionEventProvider()
                               .ReadNewJsonLinesAsync(path, 0, CancellationToken.None))
                payloads.Add(record.Payload);
            Assert.Equal(new[] { large, "{}", "[]" }, payloads);
        }
        finally { Directory.Delete(directory, true); }
    }
}
