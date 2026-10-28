// Taj's Tokens | CodexRolloutSource.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using Microsoft.Win32.SafeHandles;
using ZstdSharp;
using ZstdSharp.Unsafe;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Infrastructure.Ingestion;

/// <summary>One retained representation; verifier and parser read the same handle independently.</summary>
internal sealed class CodexRolloutSource : IAsyncDisposable
{
    private readonly FileStream _file;
    private CodexRolloutSource(string physicalPath, FileStream file)
    {
        PhysicalPath = physicalPath;
        CanonicalPath = Canonicalize(physicalPath);
        Compressed = physicalPath.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);
        _file = file;
        PhysicalIdentity = CodexSessionIngestionService.GetSourceIdentity(physicalPath, file.SafeFileHandle);
        PhysicalLength = file.Length;
        WrittenUtc = File.GetLastWriteTimeUtc(physicalPath);
        DecodedLength = Compressed ? 0 : PhysicalLength;
    }

    public string PhysicalPath { get; }
    public string CanonicalPath { get; }
    public bool Compressed { get; }
    public string PhysicalIdentity { get; }
    public long PhysicalLength { get; }
    public long CurrentPhysicalLength => RandomAccess.GetLength(_file.SafeFileHandle);
    public DateTime WrittenUtc { get; }
    public long DecodedLength { get; private set; }
    public long CurrentDecodedLength => Compressed ? DecodedLength : CurrentPhysicalLength;
    public string Encoding => Compressed ? "zstandard" : "plain";

    public static string Canonicalize(string path) => path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase)
        ? path[..^4] : path;

    public static string Resolve(string path)
    {
        string canonical = Canonicalize(path);
        return File.Exists(canonical) ? canonical : canonical + ".zst";
    }

    public static string Namespace(string path)
    {
        DirectoryInfo? directory = Directory.GetParent(Path.GetFullPath(path));
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
            if (current.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
                current.Name.Equals("archived_sessions", StringComparison.OrdinalIgnoreCase) ||
                current.Name.Equals("archive", StringComparison.OrdinalIgnoreCase))
                return current.Parent?.FullName ?? current.FullName;
        return directory?.FullName ?? Path.GetPathRoot(path)!;
    }

    public static async Task<CodexRolloutSource?> OpenAsync(string path, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string physical = Resolve(path);
            try
            {
                bool compressed = physical.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);
                // Published compressed rollouts are immutable. Refuse concurrent mutation during
                // validation/ingestion, while permitting atomic rename/delete by the source owner.
                var file = new FileStream(physical, FileMode.Open, FileAccess.Read,
                    compressed ? FileShare.Read | FileShare.Delete : FileShare.ReadWrite | FileShare.Delete,
                    1, FileOptions.Asynchronous | FileOptions.RandomAccess);
                CodexRolloutSource? source = null;
                try
                {
                    source = new(physical, file);
                    if (compressed)
                    {
                        using var validation = WorkDiagnostics.Measure(WorkPhase.CompressedValidation);
                        if (source.PhysicalLength == 0)
                            throw new CodexRolloutReadException(CodexRolloutReadFailure.TruncatedCompressedFrame);
                        // Check every frame/checksum, including a truncated final frame, before
                        // allowing any replacement/projection writes. No decoded temp file exists.
                        await using Stream decoded = source.OpenDecodedStream();
                        byte[] buffer = new byte[64 * 1024];
                        long length = 0;
                        long recordLength = 0;
                        int read;
                        while ((read = await decoded.ReadAsync(buffer, cancellationToken)) != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            length = checked(length + read);
                            int offset = 0;
                            while (offset < read)
                            {
                                int newline = buffer.AsSpan(offset, read - offset).IndexOf((byte)'\n');
                                int count = newline < 0 ? read - offset : newline;
                                recordLength += count;
                                if (recordLength > FileSystemCodexSessionEventProvider.MaxRecordBytes)
                                    throw new CodexRolloutReadException(CodexRolloutReadFailure.RecordTooLarge);
                                offset += count;
                                if (newline >= 0) { recordLength = 0; offset++; }
                            }
                        }
                        source.DecodedLength = length;
                        validation?.Complete(logicalBytes: length);
                    }
                    return source;
                }
                catch
                {
                    if (source is not null) await source.DisposeAsync();
                    else await file.DisposeAsync();
                    throw;
                }
            }
            catch (FileNotFoundException) when (attempt < 2) { await Task.Delay(15, cancellationToken); }
            catch (DirectoryNotFoundException) when (attempt < 2) { await Task.Delay(15, cancellationToken); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (ZstdException exception)
            {
                string code = exception.Code.ToString();
                throw new CodexRolloutReadException(code.Contains("windowTooLarge", StringComparison.Ordinal)
                    ? CodexRolloutReadFailure.UnsupportedWindow
                    : code.Contains("dictionary", StringComparison.OrdinalIgnoreCase)
                        ? CodexRolloutReadFailure.UnsupportedDictionary : CodexRolloutReadFailure.InvalidCompressedFrame);
            }
            catch (EndOfStreamException) { throw new CodexRolloutReadException(CodexRolloutReadFailure.TruncatedCompressedFrame); }
            catch (UnauthorizedAccessException) { throw new CodexRolloutReadException(CodexRolloutReadFailure.Inaccessible); }
            catch (IOException exception) when (exception is not CodexRolloutReadException)
            { throw new CodexRolloutReadException(CodexRolloutReadFailure.Inaccessible); }
        }
        return null;
    }

    public Stream OpenDecodedStream()
    {
        Stream raw = new HandleReadStream(_file.SafeFileHandle);
        if (!Compressed) return raw;
        try
        {
            var decoded = new DecompressionStream(raw, 64 * 1024, checkEndOfStream: true, leaveOpen: false);
            try { decoded.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 25); }
            catch { decoded.Dispose(); throw; }
            return decoded;
        }
        catch { raw.Dispose(); throw; }
    }

    public void EnsureSelected()
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            if (string.Equals(Resolve(CanonicalPath), PhysicalPath, comparison) &&
                CodexSessionIngestionService.GetSourceIdentity(PhysicalPath) == PhysicalIdentity) return;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        throw new CodexRolloutReadException(CodexRolloutReadFailure.RepresentationChanged);
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();

    private sealed class HandleReadStream(SafeFileHandle handle) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => RandomAccess.GetLength(handle);
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = RandomAccess.Read(handle, buffer, _position);
            _position += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await RandomAccess.ReadAsync(handle, buffer, _position, cancellationToken);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            if (next < 0) throw new IOException("Invalid rollout byte position.");
            return _position = next;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
