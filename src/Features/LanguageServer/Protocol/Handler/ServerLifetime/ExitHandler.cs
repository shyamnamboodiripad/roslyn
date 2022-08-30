﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.ServerLifetime;

internal class LspServiceLifeCycleManager : LifeCycleManager<RequestContext>, ILspService
{
    public LspServiceLifeCycleManager(AbstractLanguageServer<RequestContext> languageServerTarget) : base(languageServerTarget)
    {
    }
}

[Method(Methods.ExitName)]
internal class ExitHandler : ILspServiceNotificationHandler
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public ExitHandler()
    {
    }

    public bool MutatesSolutionState => true;

    public async Task HandleNotificationAsync(RequestContext requestContext, CancellationToken _)
    {
        if (requestContext.ClientCapabilities is null)
            throw new InvalidOperationException($"{Methods.InitializedName} called before {Methods.InitializeName}");
        var lifeCycleManager = requestContext.GetRequiredLspService<LspServiceLifeCycleManager>();
        await lifeCycleManager.ExitAsync().ConfigureAwait(false);
    }
}
