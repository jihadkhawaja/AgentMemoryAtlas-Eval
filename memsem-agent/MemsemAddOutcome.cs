internal sealed record MemsemAddOutcome(int Id, bool Created, bool Conflict, int[] Faded, int[] Archived);
