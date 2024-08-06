namespace EmbeddedResourceAccessGenerator;

internal record EmbeddedResourceItem(string RootNamespace, string IdentifierName, string ResourceName)
{
	public string RootNamespace { get; } = RootNamespace;
	public string ResourceName { get; } = ResourceName;
	public string IdentifierName { get; } = IdentifierName;
}