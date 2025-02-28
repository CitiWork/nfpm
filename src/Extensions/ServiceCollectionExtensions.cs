using System;
using System.Diagnostics;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NFive.PluginManager.Modules;

namespace NFive.PluginManager.Extensions;

/// <summary>
///     Extension methods for adding Entity Framework Core design-time services to an
///     <see cref="IServiceCollection" />.
/// </summary>
public static class DesignTimeServiceCollectionExtensions
{
	/// <summary>
	///     Adds the Entity Framework Core design-time services.
	/// </summary>
	/// <param name="services"> The <see cref="IServiceCollection" /> the services will be added to. </param>
	/// <param name="reporter"> Used to report design-time messages. </param>
	/// <param name="applicationServiceProviderAccessor"> An accessor to the application service provider. </param>
	/// <returns> The <paramref name="services" />. This enables chaining additional method calls. </returns>
	public static IServiceCollection AddEntityFrameworkDesignTimeServices(
		[NotNull] this IServiceCollection services,
		string[] excludeTypes,
		[CanBeNull] IOperationReporter reporter = null,
		[CanBeNull] Func<IServiceProvider> applicationServiceProviderAccessor = null)
	{
		if (reporter == null)
		{
			reporter = new OperationReporter(handler: null);
		}

		return services
			.AddSingleton<AnnotationCodeGeneratorDependencies>()
			.AddSingleton<TypeMappingSourceDependencies>()
			.AddSingleton<CSharpMigrationOperationGeneratorDependencies>()
			.AddSingleton<CSharpMigrationsGeneratorDependencies>()
			.AddSingleton<CSharpSnapshotGeneratorDependencies>()
			.AddSingleton<MigrationsCodeGeneratorDependencies>()
			.AddSingleton<ModelCodeGeneratorDependencies>()
			.AddSingleton<ProviderCodeGeneratorDependencies>()
			.AddSingleton<TypeMappingSourceDependencies>()
			.AddSingleton<RelationalTypeMappingSourceDependencies>()
			.AddSingleton<ValueConverterSelectorDependencies>()
			.AddSingleton<ICandidateNamingService, CandidateNamingService>()
			.AddSingleton<ICSharpDbContextGenerator, CSharpDbContextGenerator>()
			.AddSingleton<ICSharpEntityTypeGenerator, CSharpEntityTypeGenerator>()
			.AddSingleton<ICSharpHelper, CSharpHelper>()
			.AddSingleton<ICSharpMigrationOperationGenerator, CSharpMigrationOperationGenerator>()
			.AddSingleton<ICSharpSnapshotGenerator, CSharpSnapshotGenerator>()
			.AddSingleton<ICSharpUtilities, CSharpUtilities>()
			.AddSingleton(typeof(IDiagnosticsLogger<>), typeof(DiagnosticsLogger<>))
			.AddSingleton<IInterceptors, Interceptors>()
			.AddSingleton<DiagnosticSource>(new DiagnosticListener(DbLoggerCategory.Name))
			.AddSingleton<ILoggingOptions, LoggingOptions>()
			.AddSingleton<IMigrationsCodeGenerator, Migrate.NFiveMigrationCodeGenerator>(x => new Migrate.NFiveMigrationCodeGenerator(excludeTypes, x.GetService<MigrationsCodeGeneratorDependencies>(), x.GetService<CSharpMigrationsGeneratorDependencies>()))
			.AddSingleton<IMigrationsCodeGeneratorSelector, MigrationsCodeGeneratorSelector>()
			.AddSingleton<IModelCodeGenerator, CSharpModelGenerator>()
			.AddSingleton<IModelCodeGeneratorSelector, ModelCodeGeneratorSelector>()
			.AddSingleton<INamedConnectionStringResolver>(
				new DesignTimeConnectionStringResolver(applicationServiceProviderAccessor))
			.AddSingleton(reporter)
			.AddSingleton<IPluralizer, NullPluralizer>()
			.AddSingleton<IReverseEngineerScaffolder, ReverseEngineerScaffolder>()
			.AddSingleton<IScaffoldingModelFactory, RelationalScaffoldingModelFactory>()
			.AddSingleton<IScaffoldingTypeMapper, ScaffoldingTypeMapper>()
			.AddSingleton<IValueConverterSelector, ValueConverterSelector>()
			.AddTransient<MigrationsScaffolderDependencies>()
			.AddTransient<IMigrationsScaffolder, MigrationsScaffolder>()
			.AddTransient<ISnapshotModelProcessor, SnapshotModelProcessor>()
			.AddLogging(b =>
				b.SetMinimumLevel(LogLevel.Debug).AddProvider(new OperationLoggerProvider(reporter)));
	}
}
