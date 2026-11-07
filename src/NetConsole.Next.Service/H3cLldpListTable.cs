namespace NetConsole.Next.Service;

internal sealed record H3cLldpListTable(string Status, IReadOnlyList<H3cLldpFact> Neighbors);
