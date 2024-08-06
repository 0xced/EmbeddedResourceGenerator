namespace EmbeddedResourceAccessGenerator;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// The generator for the embedded resource access.
/// </summary>
[Generator]
public class EmbeddedResourceAccessGenerator : IIncrementalGenerator
{
	private static readonly DiagnosticDescriptor generationWarning = new DiagnosticDescriptor(
		id: "EMBRESGEN001",
		title: "Exception on generation",
		messageFormat: "Exception '{0}' {1}",
		category: "MessageExtensionGenerator",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

#if DEBUG
	private static readonly DiagnosticDescriptor logInfo = new DiagnosticDescriptor(
		id: "EMBRESGENLOG",
		title: "Log",
		messageFormat: "{0}",
		category: "MessageExtensionGenerator",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true);
#endif

	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		//Debugger.Launch();

		// We need a value provider for any addition file.
		// As soon as there is direct access to embedded resources we can change this.
		// All embedded resources are added as additional files through our build props integrated into the nuget.
		var additionaFilesProvider = context.AdditionalTextsProvider
			.Combine(context.AnalyzerConfigOptionsProvider)
			.Select((x, _) =>
				// LogicalName is truncated when it includes a semicolon, see https://github.com/dotnet/roslyn/issues/43970
				x.Right.GetOptions(x.Left).TryGetValue("build_metadata.EmbeddedResource.LogicalName", out string? logicalName)
					? logicalName
					: "")
			.Where(e => e.Length > 0)
			.Collect();

		// The root namespace value provider. Can this ever be null? So far I have not seen it.
		IncrementalValueProvider<string> rootNamespaceProvider = context.AnalyzerConfigOptionsProvider.Select((x, _) =>
			x.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? rootNamespace)
				? rootNamespace
				: "");

		// We combine the providers to generate the parameters for our source generation.
		context.RegisterSourceOutput(additionaFilesProvider.Combine(rootNamespaceProvider), this.GenerateSourceIncremental);
	}

	private void GenerateSourceIncremental(SourceProductionContext context, (ImmutableArray<string> ResourceNames, string RootNamespace) arg2)
	{
		try
		{
			this.GenerateSource(context, arg2.ResourceNames, arg2.RootNamespace);
		}
		catch (Exception e)
		{
			// We generate a diagnostic message on all internal failures.
			context.ReportDiagnostic(Diagnostic.Create(EmbeddedResourceAccessGenerator.generationWarning, Location.None,
				e.Message, e.StackTrace));
		}
	}

	private void GenerateSource(SourceProductionContext context, IReadOnlyCollection<string> resourceNames, string rootNamespace)
	{
		if (!resourceNames.Any())
		{
			return;
		}

		List<EmbeddedResourceItem> embeddedResources = new();
		Log(context, $"RootNamespace = {rootNamespace}");
		foreach (string resourceName in resourceNames)
		{
			string identifierName = this.GetValidIdentifierName(resourceName, rootNamespace);
			Log(context, $"resource = {resourceName}");
			Log(context, $"identifier = {identifierName}");
			embeddedResources.Add(new EmbeddedResourceItem(rootNamespace, identifierName, resourceName));
		}

		StringBuilder sourceBuilder = new();
		// lang=csharp
		sourceBuilder.AppendLine($$"""
				#nullable enable
				namespace {{rootNamespace}};
				using System;
				using System.Collections;
				using System.IO;
				using System.Reflection;

				/// <summary>
				/// Auto-generated class to access all embedded resources in an assembly.
				/// </summary>
				public static partial class EmbeddedResources
				{
				""");

		foreach ((string _, string identifierName, string resourceName) in embeddedResources)
		{
			// lang=csharp
			sourceBuilder.AppendLine($$"""
					/// <summary>
					/// Gets the embedded resource '{{resourceName}}' as a stream.
					/// </summary>
					/// <returns>The stream to access the embedded resource.</returns>
					public static Stream {{identifierName}}_Stream
					{
						get {
							Assembly assembly = typeof(EmbeddedResources).Assembly;
							string resource = "{{resourceName}}";
							return assembly.GetManifestResourceStream(resource)!;
						}
					}

					/// <summary>
					/// Gets the embedded resource '{{resourceName}}' as a stream-reader.
					/// </summary>
					/// <returns>The stream-reader to access the embedded resource.</returns>
					public static StreamReader {{identifierName}}_Reader
					{
						get 
						{
							Assembly assembly = typeof(EmbeddedResources).Assembly;
							string resource = "{{resourceName}}";
							return new StreamReader(assembly.GetManifestResourceStream(resource)!);
						}
					}

				""");
		}

		// lang=csharp
		sourceBuilder.AppendLine("""
					/// <summary>
					/// Gets the embedded resource's stream.
					/// </summary>
					/// <param name="resource">The embedded resource to retrieve the stream for.</param>
					/// <returns>The stream to access the embedded resource.</returns>
					public static Stream GetStream(this EmbeddedResource resource)
					{
						Assembly assembly = typeof(EmbeddedResources).Assembly;
						return assembly.GetManifestResourceStream(GetResourceName(resource))!;
					}
				
					/// <summary>
					/// Gets the embedded resource's stream-reader.
					/// </summary>
					/// <param name="resource">The embedded resource to retrieve the stream-reader for.</param>
					/// <returns>The stream-reader to access the embedded resource.</returns>
					public static StreamReader GetReader(this EmbeddedResource resource)
					{
						Assembly assembly = typeof(EmbeddedResources).Assembly;
						return new StreamReader(assembly.GetManifestResourceStream(GetResourceName(resource))!);
					}

				""");
		// lang=csharp
		sourceBuilder.AppendLine("""
					/// <summary>
					/// Gets the embedded resource's name in the format required by <c>GetManifestResourceStream</c>.
					/// </summary>
					/// <param name="resource">The embedded resource to retrieve the name for.</param>
					/// <returns>The name to access the embedded resource.</returns>
					public static string GetResourceName(this EmbeddedResource resource)
					{
						return resource switch 
						{
				""");

		foreach ((string _, string identifierName, string resourceName) in embeddedResources)
		{
			// lang=csharp
			sourceBuilder.AppendLine($$"""
							EmbeddedResource.{{identifierName}} => "{{resourceName}}",
				""");
		}

		// lang=csharp
		sourceBuilder.AppendLine("""			_ => throw new InvalidOperationException(),""");

		sourceBuilder.AppendLine("\t\t};");

		sourceBuilder.AppendLine("\t}");

		foreach (IGrouping<string, EmbeddedResourceItem> pathGrouped in embeddedResources.GroupBy(g =>
			         Path.GetDirectoryName(g.RootNamespace)))
		{
			string pathAsClassName = this.PathAsClassname(pathGrouped.Key, rootNamespace);
			if (!string.IsNullOrEmpty(pathGrouped.Key))
			{
				// lang=csharp
				sourceBuilder.AppendLine($$"""
				
					/// <summary>
					/// Gets the embedded resource's stream.
					/// </summary>
					/// <param name="resource">The embedded resource to retrieve the stream for.</param>
					/// <returns>The stream to access the embedded resource.</returns>
					public static Stream GetStream(this EmbeddedResource{{pathAsClassName}} resource)
					{
						Assembly assembly = typeof(EmbeddedResources).Assembly;
						return assembly.GetManifestResourceStream(GetResourceName(resource))!;
					}
				
					/// <summary>
					/// Gets the embedded resource's stream-reader.
					/// </summary>
					/// <param name="resource">The embedded resource to retrieve the stream-reader for.</param>
					/// <returns>The stream-reader to access the embedded resource.</returns>
					public static StreamReader GetReader(this EmbeddedResource{{pathAsClassName}} resource)
					{
						Assembly assembly = typeof(EmbeddedResources).Assembly;
						return new StreamReader(assembly.GetManifestResourceStream(GetResourceName(resource))!);
					}
				""");
				// lang=csharp
				sourceBuilder.AppendLine($$"""
				
					/// <summary>
					/// Gets the embedded resource's name in the format required by <c>GetManifestResourceStream</c>.
					/// </summary>
					/// <param name="resource">The embedded resource to retrieve the name for.</param>
					/// <returns>The name to access the embedded resource.</returns>
					public static string GetResourceName(this EmbeddedResource{{pathAsClassName}} resource)
					{
						return resource switch 
						{
				""");

				foreach ((string relativePath, string identifierName, string resourceName) in pathGrouped)
				{
					string nonPathedIdentifierName = this.GetValidIdentifierName(Path.GetFileName(relativePath), rootNamespace);
					// lang=csharp
					sourceBuilder.AppendLine($$"""
							EmbeddedResource{{pathAsClassName}}.{{nonPathedIdentifierName}} => "{{resourceName}}",
				""");
				}
				// lang=csharp
				sourceBuilder.AppendLine("""			_ => throw new InvalidOperationException(),""");

				sourceBuilder.AppendLine("\t\t};");

				sourceBuilder.AppendLine("\t}");
			}
		}

		sourceBuilder.AppendLine("}");
		// lang=csharp
		sourceBuilder.AppendLine("""
				
				/// <summary>
				/// Auto-generated enumeration for all embedded resources in the assembly.
				/// </summary>
				public enum EmbeddedResource
				{
				""");

		foreach ((string _, string identifierName, string resourceName) in embeddedResources)
		{
			// lang=csharp
			sourceBuilder.AppendLine($$"""
					/// <summary>
					/// Represents the embedded resource '{{resourceName}}'.
					/// </summary>
					{{identifierName}},
				""");
		}

		sourceBuilder.AppendLine("}");

		foreach (IGrouping<string, EmbeddedResourceItem> pathGrouped in embeddedResources.GroupBy(g =>
			         Path.GetDirectoryName(g.RootNamespace)))
		{
			string pathAsClassName = this.PathAsClassname(pathGrouped.Key, rootNamespace);
			if (!string.IsNullOrEmpty(pathGrouped.Key))
			{
				// lang=csharp
				sourceBuilder.AppendLine($$"""
						
						/// <summary>
						/// Auto-generated enumeration for all embedded resources in '{{pathGrouped.Key}}'.
						/// </summary>
						public enum EmbeddedResource{{pathAsClassName}}
						{
						""");

				foreach (EmbeddedResourceItem item in pathGrouped)
				{
					string nonPathedIdentifierName = this.GetValidIdentifierName(Path.GetFileName(item.RootNamespace), rootNamespace);
					// lang=csharp
					sourceBuilder.AppendLine($$"""
							/// <summary>
							/// Represents the embedded resource '{{Path.GetFileName(item.RootNamespace)}}' in {{pathGrouped.Key}}.
							/// </summary>
							{{nonPathedIdentifierName}},
						""");
				}

				sourceBuilder.AppendLine("}");
			}
		}

		sourceBuilder.Append("#nullable restore");

		SourceText source = SourceText.From(sourceBuilder.ToString(), Encoding.UTF8);
		context.AddSource("EmbeddedResources.generated.cs", source);
	}

	private string PathAsClassname(string path, string? rootNamespace)
	{
		return this.GetValidIdentifierName(path.Replace("\\", string.Empty).Replace("/", string.Empty), rootNamespace);
	}

	private string GetValidIdentifierName(string resourceName, string? rootNamespace)
	{
		var rootNamespacePrefix = $"{rootNamespace}.";
		if (resourceName.StartsWith(rootNamespacePrefix))
		{
			resourceName = resourceName.Substring(rootNamespacePrefix.Length);
		}
		StringBuilder sb = new(resourceName);
		sb.Replace('.', '_');

		bool first = true;
		for (int index = 0; index < resourceName.Length; index++)
		{
			char c = resourceName[index];
			bool replace;
			switch (char.GetUnicodeCategory(c))
			{
				case UnicodeCategory.LowercaseLetter:
				case UnicodeCategory.UppercaseLetter:
				case UnicodeCategory.TitlecaseLetter:
				case UnicodeCategory.ModifierLetter:
				case UnicodeCategory.OtherLetter:
					replace = false;
					break;
				case UnicodeCategory.ConnectorPunctuation:
				case UnicodeCategory.DecimalDigitNumber:
				case UnicodeCategory.Format:
				case UnicodeCategory.LetterNumber:
				case UnicodeCategory.NonSpacingMark:
				case UnicodeCategory.SpacingCombiningMark:
					// Only valid in non-leading position.
					replace = first;
					break;
				default:
					replace = true;
					break;
			}

			if (replace)
			{
				sb[index] = '_';
			}

			first = false;
		}

		return sb.ToString();
	}

	[Conditional("DEBUG")]
	private void Log(SourceProductionContext context, string log)
	{
		context.ReportDiagnostic(Diagnostic.Create(EmbeddedResourceAccessGenerator.logInfo, Location.None, log));
	}
}