internal sealed record MemsemMemory(int Id, string Subject, string Predicate, string Object, string[] Tags, string? Theme, double Confidence, double Importance)
{
	public string Text => $"{Subject} → {Predicate} → {Object}";
}
