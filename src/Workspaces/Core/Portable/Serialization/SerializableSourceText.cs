﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.Host.TemporaryStorageService;

#if DEBUG
using System.Linq;
#endif

namespace Microsoft.CodeAnalysis.Serialization;

#pragma warning disable CA1416 // Validate platform compatibility

/// <summary>
/// Represents a <see cref="SourceText"/> which can be serialized for sending to another process. The text is not
/// required to be a live object in the current process, and can instead be held in temporary storage accessible by
/// both processes.
/// </summary>
internal sealed class SerializableSourceText
{
    /// <summary>
    /// The storage location for <see cref="SourceText"/>.
    /// </summary>
    /// <remarks>
    /// Exactly one of <see cref="_storage"/> or <see cref="_text"/> will be non-<see langword="null"/>.
    /// </remarks>
    private readonly TemporaryTextStorage? _storage;

    /// <summary>
    /// The <see cref="SourceText"/> in the current process.
    /// </summary>
    /// <remarks>
    /// <inheritdoc cref="Storage"/>
    /// </remarks>
    private readonly SourceText? _text;

    /// <summary>
    /// Weak reference to a SourceText computed from <see cref="_storage"/>.  Useful so that if multiple requests
    /// come in for the source text, the same one can be returned as long as something is holding it alive.
    /// </summary>
    private readonly WeakReference<SourceText?> _computedText = new(target: null);

    /// <summary>
    /// Checksum of the contents (see <see cref="SourceText.GetContentHash"/>) of the text.
    /// </summary>
    public readonly Checksum ContentChecksum;

    public SerializableSourceText(TemporaryTextStorage storage, ImmutableArray<byte> contentHash)
        : this(storage, text: null, contentHash)
    {
    }

    public SerializableSourceText(SourceText text, ImmutableArray<byte> contentHash)
        : this(storage: null, text, contentHash)
    {
    }

    private SerializableSourceText(TemporaryTextStorage? storage, SourceText? text, ImmutableArray<byte> contentHash)
    {
        Debug.Assert(storage is null != text is null);

        _storage = storage;
        _text = text;
        ContentChecksum = Checksum.Create(contentHash);

#if DEBUG
        var computedContentHash = TryGetText()?.GetContentHash() ?? _storage!.ContentHash;
        Debug.Assert(contentHash.SequenceEqual(computedContentHash));
#endif
    }

    /// <summary>
    /// Returns the strongly referenced SourceText if we have it, or tries to retrieve it from the weak reference if
    /// it's still being held there.
    /// </summary>
    /// <returns></returns>
    private SourceText? TryGetText()
        => _text ?? _computedText.GetTarget();

    public async ValueTask<SourceText> GetTextAsync(CancellationToken cancellationToken)
    {
        var text = TryGetText();
        if (text != null)
            return text;

        // Read and cache the text from the storage object so that other requests may see it if still kept alive by something.
        text = await _storage!.ReadTextAsync(cancellationToken).ConfigureAwait(false);
        _computedText.SetTarget(text);
        return text;
    }

    public SourceText GetText(CancellationToken cancellationToken)
    {
        var text = TryGetText();
        if (text != null)
            return text;

        // Read and cache the text from the storage object so that other requests may see it if still kept alive by something.
        text = _storage!.ReadText(cancellationToken);
        _computedText.SetTarget(text);
        return text;
    }

    public static ValueTask<SerializableSourceText> FromTextDocumentStateAsync(
        TextDocumentState state, CancellationToken cancellationToken)
    {
        if (state.TextAndVersionSource.TextLoader is SerializableSourceTextLoader serializableLoader)
        {
            // If we're already pointing at a serializable loader, we can just use that directly.
            return new(serializableLoader.SerializableSourceText);
        }
        else if (state.Storage is TemporaryTextStorage storage)
        {
            // Otherwise, if we're pointing at a memory mapped storage location, we can create the source text that directly wraps that.
            return new(new SerializableSourceText(storage, storage.ContentHash));
        }
        else
        {
            // Otherwise, the state object has reified the text into some other form, and dumped any original
            // information on how it got it.  In that case, we create a new text instance to represent the serializable
            // source text out of.

            return SpecializedTasks.TransformWithoutIntermediateCancellationExceptionAsync(
                static (state, cancellationToken) => state.GetTextAsync(cancellationToken),
                static (text, _) => new SerializableSourceText(text, text.GetContentHash()),
                state,
                cancellationToken);
        }
    }

    public void Serialize(ObjectWriter writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_storage is not null)
        {
            writer.WriteInt32((int)_storage.ChecksumAlgorithm);
            writer.WriteEncoding(_storage.Encoding);
            writer.WriteByteArray(ImmutableCollectionsMarshal.AsArray(_storage.ContentHash)!);

            writer.WriteInt32((int)SerializationKinds.MemoryMapFile);
            writer.WriteString(_storage.Name);
            writer.WriteInt64(_storage.Offset);
            writer.WriteInt64(_storage.Size);
        }
        else
        {
            RoslynDebug.AssertNotNull(_text);

            writer.WriteInt32((int)_text.ChecksumAlgorithm);
            writer.WriteEncoding(_text.Encoding);
            writer.WriteByteArray(ImmutableCollectionsMarshal.AsArray(_text.GetContentHash())!);

            writer.WriteInt32((int)SerializationKinds.Bits);
            _text.WriteTo(writer, cancellationToken);
        }
    }

    public static SerializableSourceText Deserialize(
        ObjectReader reader,
        ITemporaryStorageServiceInternal storageService,
        ITextFactoryService textService,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var checksumAlgorithm = (SourceHashAlgorithm)reader.ReadInt32();
        var encoding = reader.ReadEncoding();
        var contentHash = ImmutableCollectionsMarshal.AsImmutableArray(reader.ReadByteArray());

        var kind = (SerializationKinds)reader.ReadInt32();
        Contract.ThrowIfFalse(kind is SerializationKinds.Bits or SerializationKinds.MemoryMapFile);

        if (kind == SerializationKinds.MemoryMapFile)
        {
            var storage2 = (TemporaryStorageService)storageService;

            var name = reader.ReadRequiredString();
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();

            var storage = storage2.AttachTemporaryTextStorage(name, offset, size, checksumAlgorithm, encoding, contentHash);
            return new SerializableSourceText(storage, contentHash);
        }
        else
        {
            return new SerializableSourceText(
                SourceTextExtensions.ReadFrom(textService, reader, encoding, checksumAlgorithm, cancellationToken),
                contentHash);
        }
    }

    public TextLoader ToTextLoader(string? filePath)
        => new SerializableSourceTextLoader(this, filePath);

    /// <summary>
    /// A <see cref="TextLoader"/> that wraps a <see cref="SerializableSourceText"/> and provides access to the text in
    /// a deferred fashion.  In practice, during a host and OOP sync, while all the documents will be 'serialized' over
    /// to OOP, the actual contents of the documents will only need to be loaded depending on which files are open, and
    /// thus what compilations and trees are needed.  As such, we want to be able to lazily defer actually getting the
    /// contents of the text until it's actually needed.  This loader allows us to do that, allowing the OOP side to
    /// simply point to the segments in the memory-mapped-file the host has dumped its text into, and only actually
    /// realizing the real text values when they're needed.
    /// </summary>
    private sealed class SerializableSourceTextLoader : TextLoader
    {
        public readonly SerializableSourceText SerializableSourceText;
        private readonly AsyncLazy<TextAndVersion> _lazyTextAndVersion;

        public SerializableSourceTextLoader(
            SerializableSourceText serializableSourceText,
            string? filePath)
        {
            SerializableSourceText = serializableSourceText;
            var version = VersionStamp.Create();

            this.FilePath = filePath;
            _lazyTextAndVersion = AsyncLazy.Create(
                async static (tuple, cancellationToken) =>
                    TextAndVersion.Create(await tuple.serializableSourceText.GetTextAsync(cancellationToken).ConfigureAwait(false), tuple.version, tuple.filePath),
                static (tuple, cancellationToken) =>
                    TextAndVersion.Create(tuple.serializableSourceText.GetText(cancellationToken), tuple.version, tuple.filePath),
                (serializableSourceText, version, filePath));
        }

        internal override string? FilePath { get; }

        public override Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
            => _lazyTextAndVersion.GetValueAsync(cancellationToken);

        internal override TextAndVersion LoadTextAndVersionSynchronously(LoadTextOptions options, CancellationToken cancellationToken)
            => _lazyTextAndVersion.GetValue(cancellationToken);
    }
}
