using System.Text.RegularExpressions;

namespace VlmHub.Balancer.Orchestration;

internal static class VlmContentSanitizer
{
    private static readonly Regex OpeningFenceRegex = new(
        @"```(?:markdown|md|text)?",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    private static readonly Regex LocationRegex = new(
        @"<loc_\d+>",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    private static readonly Regex SpecialTokenRegex = new(
        @"<\|.*?\|>",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Elimina contenedores HTML cuyo único contenido es una imagen.
    ///
    /// Ejemplo:
    /// <div style="text-align: center;"><img src="..." /></div>
    /// </summary>
    private static readonly Regex HtmlImageContainerRegex = new(
        @"<div\b[^>]*>\s*<img\b[^>]*?/?>\s*</div>",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant |
        RegexOptions.Singleline);

    /// <summary>
    /// Elimina imágenes HTML que aparezcan sin un div contenedor.
    ///
    /// Ejemplo:
    /// <img src="imgs/image.jpg" alt="Image" />
    /// </summary>
    private static readonly Regex HtmlImageRegex = new(
        @"<img\b[^>]*?/?>",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant |
        RegexOptions.Singleline);

    /// <summary>
    /// Elimina imágenes Markdown.
    ///
    /// Ejemplo:
    /// ![Image](imgs/image.jpg)
    /// </summary>
    private static readonly Regex MarkdownImageRegex = new(
        @"!\[[^\]]*\]\([^)]+\)",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Evita dejar grandes cantidades de líneas vacías después de limpiar.
    /// </summary>
    private static readonly Regex ExcessBlankLinesRegex = new(
        @"(?:\r?\n){3,}",
        RegexOptions.CultureInvariant);

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = text;

        // Eliminar fences Markdown.
        cleaned = OpeningFenceRegex.Replace(cleaned, string.Empty);

        cleaned = cleaned.Replace(
            "```",
            string.Empty,
            StringComparison.Ordinal);

        // Eliminar marcadores propios de modelos OCR/VLM.
        cleaned = LocationRegex.Replace(cleaned, " ");

        cleaned = SpecialTokenRegex.Replace(cleaned, " ");

        cleaned = cleaned.Replace(
            "<END_OF_OCR>",
            " ",
            StringComparison.OrdinalIgnoreCase);

        // Eliminar referencias a imágenes.
        //
        // Primero el contenedor completo para no dejar:
        // <div></div>
        cleaned = HtmlImageContainerRegex.Replace(
            cleaned,
            string.Empty);

        // Después cualquier imagen HTML que haya quedado suelta.
        cleaned = HtmlImageRegex.Replace(
            cleaned,
            string.Empty);

        // Y finalmente imágenes Markdown.
        cleaned = MarkdownImageRegex.Replace(
            cleaned,
            string.Empty);

        // Normalizar saltos de línea producidos por la limpieza.
        cleaned = ExcessBlankLinesRegex.Replace(
            cleaned,
            Environment.NewLine + Environment.NewLine);

        return cleaned.Trim();
    }
}