using Anonimizador.Detection.Patterns;
using Anonimizador.Documents.Cpf;
using Anonimizador.Transformation;

namespace Anonimizador.Detection.Detectors;

/// <summary>Encontra CPFs com dígitos verificadores válidos, com ou sem máscara.</summary>
internal sealed class CpfDetector : PatternDetector
{
    private CpfDetector()
    {
    }

    /// <summary>A instância única (o detector não tem estado).</summary>
    public static CpfDetector Instance { get; } = new();

    /// <inheritdoc/>
    public override string Type => DocumentTypeNames.Cpf;

    /// <inheritdoc/>
    public override void Collect(string text, List<TextMatch> matches) =>
        Collect(DocumentPatterns.Cpf(), text, matches, static candidate => CpfFormat.TryParse(candidate, ValueKind.Text, out _));
}
