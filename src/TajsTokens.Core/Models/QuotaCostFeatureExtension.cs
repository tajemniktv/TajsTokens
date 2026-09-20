// Taj's Tokens | QuotaCostFeatureExtension.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>Explicit experimental feature input. The live accounting path supplies none.</summary>
public sealed record QuotaCostFeatureExtension(string[] Names, Func<QuotaCostObservation, double[]> Values);