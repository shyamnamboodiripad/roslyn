﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Utilities;

namespace Microsoft.CodeAnalysis.QuickInfo
{
    /// <summary>
    /// Base class for <see cref="QuickInfoService"/>'s that delegate to <see cref="QuickInfoProvider"/>'s.
    /// </summary>
    internal abstract class QuickInfoServiceWithProviders : QuickInfoService
    {
        private readonly Workspace _workspace;
        private readonly string _language;
        private ImmutableArray<QuickInfoProvider> _providers;

        protected QuickInfoServiceWithProviders(Workspace workspace, string language)
        {
            _workspace = workspace;
            _language = language;
        }

        private ImmutableArray<QuickInfoProvider> GetProviders()
        {
            if (_providers.IsDefault)
            {
                var mefExporter = (IMefHostExportProvider)_workspace.Services.HostServices;

                var providers = ExtensionOrderer
                    .Order(mefExporter.GetExports<QuickInfoProvider, QuickInfoProviderMetadata>()
                        .Where(lz => lz.Metadata.Language == _language))
                    .Select(lz => lz.Value)
                    .ToImmutableArray();

                ImmutableInterlocked.InterlockedCompareExchange(ref _providers, providers, default);
            }

            return _providers;
        }

        public override Task<QuickInfoItem?> GetQuickInfoAsync(Document document, int position, CancellationToken cancellationToken)
        {
            return GetQuickInfoAsync(_workspace, GetProviders(), document, position, cancellationToken);
        }

        private static async Task<QuickInfoItem?> GetQuickInfoAsync(Workspace workspace, ImmutableArray<QuickInfoProvider> providers, Document document, int position, CancellationToken cancellationToken)
        {
            var extensionManager = workspace.Services.GetRequiredService<IExtensionManager>();

            // returns the first non-empty quick info found (based on provider order)
            foreach (var provider in providers)
            {
                try
                {
                    if (!extensionManager.IsDisabled(provider))
                    {
                        var context = new QuickInfoContext(document, position, cancellationToken);

                        var info = await provider.GetQuickInfoAsync(context).ConfigureAwait(false);
                        if (info != null)
                        {
                            return info;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e) when (extensionManager.CanHandleException(provider, e))
                {
                    extensionManager.HandleException(provider, e);
                }
            }

            return null;
        }
    }
}
