// Taj's Tokens | AccountLocalRidge.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Services;

/// <summary>Small standardized ridge fit. Scaling is learned from the training prefix only.</summary>
internal sealed record AccountLocalRidge(double[] Means, double[] Scales, double[] Coefficients)
{
    public double Predict(double[] features)
    {
        double value = Coefficients[0];
        for (int i = 0; i < Means.Length; i++) value += Coefficients[i + 1] * (features[i] - Means[i]) / Scales[i];
        return value;
    }

    public static AccountLocalRidge? Fit(IReadOnlyList<double[]> features, IReadOnlyList<double> targets, double penalty = 1)
    {
        if (features.Count == 0 || features.Count != targets.Count) return null;
        int width = features[0].Length;
        if (features.Any(x => x.Length != width || x.Any(v => !double.IsFinite(v))) || targets.Any(v => !double.IsFinite(v))) return null;
        double[] means = Enumerable.Range(0, width).Select(i => features.Average(x => x[i])).ToArray();
        double[] scales = Enumerable.Range(0, width)
            .Select(i => Math.Max(0.000001, Math.Sqrt(features.Average(x => Math.Pow(x[i] - means[i], 2))))).ToArray();
        int size = width + 1;
        double[,] matrix = new double[size, size];
        double[] vector = new double[size];
        for (int row = 0; row < features.Count; row++)
        {
            double[] x = new double[size];
            x[0] = 1;
            for (int i = 0; i < width; i++) x[i + 1] = (features[row][i] - means[i]) / scales[i];
            for (int i = 0; i < size; i++)
            {
                vector[i] += x[i] * targets[row];
                for (int j = 0; j < size; j++) matrix[i, j] += x[i] * x[j];
            }
        }
        for (int i = 1; i < size; i++) matrix[i, i] += penalty;
        for (int column = 0; column < size; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < size; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                    pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-10) return null;
            for (int j = column; j < size; j++) (matrix[column, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[column, j]);
            (vector[column], vector[pivot]) = (vector[pivot], vector[column]);
            double divisor = matrix[column, column];
            for (int j = column; j < size; j++) matrix[column, j] /= divisor;
            vector[column] /= divisor;
            for (int row = 0; row < size; row++)
            {
                if (row == column) continue;
                double factor = matrix[row, column];
                for (int j = column; j < size; j++) matrix[row, j] -= factor * matrix[column, j];
                vector[row] -= factor * vector[column];
            }
        }
        return vector.All(double.IsFinite) ? new AccountLocalRidge(means, scales, vector) : null;
    }
}