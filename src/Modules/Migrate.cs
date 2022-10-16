using CommandLine;
using EnvDTE;
using NFive.PluginManager.Utilities;
using NFive.SDK.Server;
using NFive.SDK.Server.Storage;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using NFive.PluginManager.Extensions;

namespace NFive.PluginManager.Modules
{
	/// <summary>
	/// Create a NFive database migration.
	/// </summary>
	[Verb("migrate", HelpText = "Create a NFive database migration.")]
	internal class Migrate : Module
	{
		private bool existingInstance = true;

		[Option("name", Required = true, HelpText = "Migration name.")]
		public string Name { get; set; } = null;

		[Option("db", Required = true, HelpText = "MySQL database connection string.")]
		public string Database { get; set; } = null;

		[Option("sln", Required = false, HelpText = "Visual Studio SLN solution file.")]
		public string Sln { get; set; }

		[Option("migrate", Required = false, HelpText = "Run existing migrations if necessary.")]
		public bool RunMigrations { get; set; } = false;

		[Option("sdk", Required = false, HelpText = "Internal use only, do not exclude SDK types.")]
		public bool Sdk { get; set; } = false;

		[SuppressMessage("ReSharper", "SuspiciousTypeConversion.Global")]
		[SuppressMessage("ReSharper", "ImplicitlyCapturedClosure")]
		public override async Task<int> Main()
		{
			try
			{
				Environment.CurrentDirectory = PathManager.FindResource();
			}
			catch (FileNotFoundException ex)
			{
				Console.WriteLine(ex.Message);
				Console.WriteLine("Use `nfpm scaffold` to generate a NFive plugin in this directory");

				return 1;
			}

			AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
			{
				if (args.Name.Contains(".resources")) return null;

				var fileName = args.Name.Substring(0, args.Name.IndexOf(",", StringComparison.InvariantCultureIgnoreCase)) + ".dll";

				if (File.Exists(fileName)) return Assembly.Load(File.ReadAllBytes(fileName));

				var path = Directory.EnumerateFiles("plugins", "*.dll", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f) == fileName);

				if (string.IsNullOrEmpty(path)) throw new FileLoadException(args.Name);

				return Assembly.Load(File.ReadAllBytes(path));
			};

			DTE dte = null;

			try
			{
				if (!File.Exists(this.Sln)) this.Sln = Directory.EnumerateFiles(Environment.CurrentDirectory, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
				if (this.Sln == null || !File.Exists(this.Sln)) this.Sln = Input.String("Visual Studio SLN solution file");

				Console.Write("Searching for existing Visual Studio instance...");

				dte = VisualStudio.GetInstances().FirstOrDefault(env => env.Solution.FileName == this.Sln);

				if (dte != default)
				{
					Console.WriteLine(" found");
				}
				else
				{
					Console.WriteLine(" not found");
					Console.WriteLine("Starting new Visual Studio instance...");

					dte = (DTE)Activator.CreateInstance(Type.GetTypeFromProgID("VisualStudio.DTE", true), true);

					this.existingInstance = false;
				}

				Console.WriteLine("Opening solution");

				var solution = Retry.Do(() => dte.Solution);

				if (!Retry.Do(() => solution.IsOpen)) Retry.Do(() => solution.Open(this.Sln));

				Console.WriteLine("Building solution");

				solution.SolutionBuild.Build(true);

				Console.WriteLine("Searching for projects");

				var pp = Retry.Do(() => solution.Projects.Cast<Project>().ToList());

				var ppp = Retry.Do(() => pp.Where(p => !string.IsNullOrWhiteSpace(p.FullName)).ToList());

				foreach (var project in ppp)
				{
					Console.WriteLine($"  Analyzing project {Retry.Do(() => project.Name)}...");

					var projectPath = Path.GetDirectoryName(Retry.Do(() => project.FullName)) ?? string.Empty;
					var outputPath = Path.GetFullPath(Path.Combine(projectPath, Retry.Do(() => project.ConfigurationManager.ActiveConfiguration.Properties.Item("OutputPath").Value.ToString()), Retry.Do(() => project.Properties.Item("OutputFileName").Value.ToString())));

					var asm = Assembly.Load(File.ReadAllBytes(outputPath));
					if (!this.Sdk && asm.GetCustomAttribute<ServerPluginAttribute>() == null) continue;

					var contextType = asm.DefinedTypes.FirstOrDefault(t => t.BaseType != null && t.BaseType.IsGenericType && t.BaseType.GetGenericTypeDefinition() == typeof(EFContext<>));
					if (contextType == default) continue;

					Console.WriteLine($"    Loaded {outputPath}");

					Console.WriteLine($"    Found DB context: {contextType.Name}");

					var props = contextType
						.GetProperties()
						.Where(p =>
							p.CanRead &&
							p.CanWrite &&
							p.PropertyType.IsGenericType &&
							p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>) &&
							p.PropertyType.GenericTypeArguments.Any(t => !string.IsNullOrEmpty(t.Namespace) && t.Namespace.StartsWith("NFive.SDK."))) // TODO
						.Select(t => $"{t.Name}") // TODO
						.ToArray();

					if (!this.Sdk) Console.WriteLine($"    Excluding tables: {string.Join(", ", props)}");

					var migrationsPath = "Migrations";

					if (!Directory.Exists(Path.Combine(projectPath, migrationsPath))) migrationsPath = Input.String("Migration source code folder", "Migrations"); // TODO: Validate

					var @namespace = $"{project.Properties.Item("RootNamespace").Value}.{migrationsPath}";

					if (asm.DefinedTypes.Any(t => t.BaseType != null && t.BaseType == typeof(Migration) && t.Namespace == @namespace && t.Name == this.Name))
					{
						throw new Exception($"A migration named \"{this.Name}\" already exists at \"{@namespace}.{this.Name}\", please use another migration name.");
					}

					Console.WriteLine("    Generating migration...");

					var serviceCollection = new ServiceCollection();
					serviceCollection.AddTransient<IMigrationsCodeGenerator, NFiveMigrationCodeGenerator>();
					serviceCollection.AddEntityFrameworkDesignTimeServices(this.Sdk ? new string[] {} : props);

					serviceCollection.AddDbContextDesignTimeServices((DbContext)Activator.CreateInstance(contextType));
					var serviceProvider = serviceCollection.BuildServiceProvider();
					var migrationsScaffolder = serviceProvider.GetService<IMigrationsScaffolder>();

					if (this.RunMigrations)
					{
						var context = serviceProvider.GetService<DbContext>();
						
						if ((await context.Database.GetPendingMigrationsAsync()).Any())
						{
							Console.WriteLine("    Running existing migrations...");

							await context.Database.MigrateAsync();

							foreach (var migration in await context.Database.GetAppliedMigrationsAsync())
							{
								Console.WriteLine($"        Applied migration: {migration}");
							}
						}
					}

					Console.WriteLine("    Scaffolding migration...");

					var src = migrationsScaffolder.ScaffoldMigration(this.Name, @namespace);

					var migrationFile = Path.Combine(projectPath, migrationsPath, $"{src.MigrationId}{src.FileExtension}");
					var metadataFile = Path.Combine(projectPath, migrationsPath,
						$"{src.MigrationId}.Designer{src.FileExtension}");

					Console.WriteLine($"    Writing migration: {migrationFile}");

					File.WriteAllText(migrationFile, src.MigrationCode);

					Console.WriteLine($"    Writing migration metadata: {metadataFile}");

					File.WriteAllText(metadataFile, src.MetadataCode);

					Console.WriteLine("    Updating project...");

					project.ProjectItems.AddFromFile(migrationFile);
					project.ProjectItems.AddFromFile(metadataFile);
					project.Save();
				}

				Console.WriteLine("Building solution...");

				solution.SolutionBuild.Build(true);

				if (!this.existingInstance)
				{
					Console.WriteLine("Quitting Visual Studio instance");

					dte.Quit();
				}

				Console.WriteLine("Done");

				return await Task.FromResult(0);
			}
			catch (ReflectionTypeLoadException ex)
			{
				Console.WriteLine(ex.Message);
				Console.WriteLine(string.Join(Environment.NewLine, ex.LoaderExceptions.Select(e => e.Message)));

				return 1;
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);

				return 1;
			}
			finally
			{
				if (!this.existingInstance) dte.Quit();
			}
		}

		/// <inheritdoc />
		public class NFiveMigrationCodeGenerator : CSharpMigrationsGenerator
		{
			protected IEnumerable<string> ExcludedModels;

			/// <inheritdoc />
			public NFiveMigrationCodeGenerator(IEnumerable<string> excludedModels, MigrationsCodeGeneratorDependencies dependencies, CSharpMigrationsGeneratorDependencies cSharpDependencies)
			:base(dependencies, cSharpDependencies)
			{
				this.ExcludedModels = excludedModels;
			}
			
			/// <summary>
			///     Generates the migration code.
			/// </summary>
			/// <param name="migrationNamespace">The migration's namespace.</param>
			/// <param name="migrationName">The migration's name.</param>
			/// <param name="upOperations">The migration's up operations.</param>
			/// <param name="downOperations">The migration's down operations.</param>
			/// <returns>The migration code.</returns>
			public override string GenerateMigration(
				string migrationNamespace,
				string migrationName,
				IReadOnlyList<MigrationOperation> upOperations,
				IReadOnlyList<MigrationOperation> downOperations)
			{
				var filteredUp = FilterMigrationOperations(upOperations).ToImmutableList();
				var filteredDown = FilterMigrationOperations(downOperations).ToImmutableList();

				return InternalGenerateMigration(migrationNamespace, migrationName, filteredUp, filteredDown);
			}

			private string InternalGenerateMigration(string migrationNamespace, string migrationName,
				IReadOnlyList<MigrationOperation> upOperations, IReadOnlyList<MigrationOperation> downOperations)
			{
				var builder = new IndentedStringBuilder();
				var namespaces = new List<string> { "Microsoft.EntityFrameworkCore.Migrations" };
				namespaces.AddRange(GetNamespaces(upOperations.Concat(downOperations)));
				foreach (var n in namespaces.OrderBy(x => x, new NamespaceComparer()).Distinct())
				{
					builder
						.Append("using ")
						.Append(n)
						.AppendLine(";");
				}
				builder.AppendLine("using System.CodeDom.Compiler;");
				builder.AppendLine();

				// Suppress "Prefer jagged arrays over multidimensional" when we have a seeding operation with a multidimensional array
				if (HasMultidimensionalArray(upOperations.Concat(downOperations)))
				{
					builder
						.AppendLine()
						.AppendLine("#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional");
				}

				if (!string.IsNullOrEmpty(migrationNamespace))
				{
					builder
						.AppendLine()
						.Append("namespace ").AppendLine(this.CSharpDependencies.CSharpHelper.Namespace(migrationNamespace))
						.AppendLine("{")
						.IncrementIndent();
				}

				builder
					.AppendLine("/// <inheritdoc />")
					.AppendLine($"[GeneratedCode(\"NFive.Migration\", \"{typeof(NFiveMigrationCodeGenerator).GetTypeInfo().Assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>().Single().InformationalVersion}\")]")
					.Append("public partial class ").Append(CSharpDependencies.CSharpHelper.Identifier(migrationName)).AppendLine(" : Migration")
					.AppendLine("{");
				using (builder.Indent())
				{
					builder
						.AppendLine("/// <inheritdoc />")
						.AppendLine("protected override void Up(MigrationBuilder migrationBuilder)")
						.AppendLine("{");
					using (builder.Indent())
					{
						CSharpDependencies.CSharpMigrationOperationGenerator.Generate("migrationBuilder", upOperations, builder);
					}

					builder
						.AppendLine()
						.AppendLine("}")
						.AppendLine()
						.AppendLine("/// <inheritdoc />")
						.AppendLine("protected override void Down(MigrationBuilder migrationBuilder)")
						.AppendLine("{");
					using (builder.Indent())
					{
						CSharpDependencies.CSharpMigrationOperationGenerator.Generate("migrationBuilder", downOperations, builder);
					}

					builder
						.AppendLine()
						.AppendLine("}");
				}

				builder.AppendLine("}");

				if (!string.IsNullOrEmpty(migrationNamespace))
				{
					builder
						.DecrementIndent()
						.AppendLine("}");
				}

				return builder.ToString();
			}

			private bool HasMultidimensionalArray(IEnumerable<MigrationOperation> operations)
			{
				return operations.Any(
					o =>
						(o is InsertDataOperation insertDataOperation
						 && IsMultidimensional(insertDataOperation.Values))
						|| (o is UpdateDataOperation updateDataOperation
						    && (IsMultidimensional(updateDataOperation.Values) || IsMultidimensional(updateDataOperation.KeyValues)))
						|| (o is DeleteDataOperation deleteDataOperation
						    && IsMultidimensional(deleteDataOperation.KeyValues)));

				static bool IsMultidimensional(Array array)
					=> array.GetLength(0) > 1 && array.GetLength(1) > 1;
			}
			
			private IEnumerable<MigrationOperation> FilterMigrationOperations(IReadOnlyList<MigrationOperation> operations)
			{
				var exceptions = new IEnumerable<MigrationOperation>[]
				{
					operations.OfType<CreateTableOperation>().Where(op => this.ExcludedModels.Contains(op.Name)),
					operations.OfType<DropTableOperation>().Where(op => this.ExcludedModels.Contains(op.Name)),

					operations.OfType<AddForeignKeyOperation>().Where(op => this.ExcludedModels.Contains(op.PrincipalTable)),
					operations.OfType<DropForeignKeyOperation>().Where(op => this.ExcludedModels.Contains(op.Table)),

					operations.OfType<CreateIndexOperation>().Where(op => this.ExcludedModels.Contains(op.Table)),
					operations.OfType<DropIndexOperation>().Where(op => this.ExcludedModels.Contains(op.Table))
				};

				return operations.Except(exceptions.SelectMany(o => o));
			}

			public override string FileExtension { get; } = ".cs";
		}
	}
}
