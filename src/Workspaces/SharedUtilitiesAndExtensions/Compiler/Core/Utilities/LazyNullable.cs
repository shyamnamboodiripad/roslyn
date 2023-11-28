﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using Roslyn.Utilities;

/// <summary>
/// A lazy version of <see cref="Nullable{T}"/> which uses the same space as a <see cref="Nullable{T}"/>.
/// </summary>
/// <typeparam name="T"></typeparam>
internal struct LazyNullable<T>
    where T : struct
{
    /// <summary>
    /// One of three values:
    /// <list type="bullet">
    /// <item>0. <see cref="_value"/> is not initialized yet.</item>
    /// <item>1. <see cref="_value"/> is currently being initialized by some thread.</item>
    /// <item>2. <see cref="_value"/> has been initialized.</item>
    /// </list>
    /// </summary>
    private int _initialized;
    private T _value;

    /// <summary>
    /// Ensure that the given target value is initialized in a thread-safe manner.
    /// </summary>
    /// <param name="valueFactory">A factory delegate to create a new instance of the target value. Note that this
    /// delegate may be called more than once by multiple threads, but only one of those values will successfully be
    /// written to the target.</param>
    /// <returns>The target value.</returns>
    public T Initialize<TArg>(Func<TArg, T> valueFactory, TArg arg)
        => ReadIfInitialized() ?? GetOrStore(valueFactory(arg));

    private T? ReadIfInitialized()
        => Volatile.Read(ref _initialized) == 2 ? _value : null;

    private T GetOrStore(T value)
    {
        SpinWait spinWait = default;
        while (true)
        {
            switch (Interlocked.CompareExchange(ref _initialized, 1, 0))
            {
                case 0:
                    // This thread is responsible for assigning the value to target
                    _value = value;
                    Volatile.Write(ref _initialized, 2);
                    return value;

                case 1:
                    // Another thread has already claimed responsibility for writing to target, but that write is
                    // not yet complete.  Spin until we see the value finally transition to the '2' state.
                    spinWait.SpinOnce();
                    continue;

                case 2:
                    // Another thread has already completed writing to 'target'.  Because we use a CompareExchange, we
                    // can only get here once the VolatileWrite to _initialized has happened.  Which means the write to
                    // _value must be seen (as writes can't be reordered across these calls).
                    return ReadIfInitialized() ?? throw ExceptionUtilities.Unreachable();

                case var unexpectedValue:
                    throw ExceptionUtilities.UnexpectedValue(unexpectedValue);
            }
        }
    }
}
