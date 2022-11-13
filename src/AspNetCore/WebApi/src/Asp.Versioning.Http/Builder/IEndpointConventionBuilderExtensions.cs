﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.

namespace Microsoft.AspNetCore.Builder;

using Asp.Versioning;
using Asp.Versioning.Builder;
using Asp.Versioning.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Runtime.CompilerServices;
using static Asp.Versioning.ApiVersionParameterLocation;
using static Asp.Versioning.ApiVersionProviderOptions;

/// <summary>
/// Provides extension methods for <see cref="IEndpointConventionBuilder"/> and <see cref="IVersionedEndpointRouteBuilder"/>.
/// </summary>
[CLSCompliant( false )]
public static class IEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Applies the specified API version set to the endpoint.
    /// </summary>
    /// <typeparam name="TBuilder">The type of builder.</typeparam>
    /// <param name="builder">The extended builder.</param>
    /// <param name="apiVersionSet">The <see cref="ApiVersionSet">API version set</see> the endpoint will use.</param>
    /// <returns>The original <paramref name="builder"/>.</returns>
    public static TBuilder WithApiVersionSet<TBuilder>(
        this TBuilder builder,
        ApiVersionSet apiVersionSet )
        where TBuilder : notnull, IEndpointConventionBuilder
    {
        if ( apiVersionSet == null )
        {
            throw new ArgumentNullException( nameof( apiVersionSet ) );
        }

        builder.Add( endpoint => endpoint.Metadata.Add( apiVersionSet ) );
        builder.Finally( FinalizeEndpoints );

        return builder;
    }

    /// <summary>
    /// Applies the specified API version set to the endpoint group.
    /// </summary>
    /// <typeparam name="TBuilder">The type of builder.</typeparam>
    /// <param name="builder">The extended builder.</param>
    /// <param name="name">The optional name associated with the builder.</param>
    /// <returns>A new <see cref="IVersionedEndpointRouteBuilder"/> instance.</returns>
    public static IVersionedEndpointRouteBuilder WithApiVersionSet<TBuilder>( this TBuilder builder, string? name = default )
        where TBuilder : notnull, IEndpointRouteBuilder, IEndpointConventionBuilder
    {
        if ( builder is IVersionedEndpointRouteBuilder versionedBuilder )
        {
            return versionedBuilder;
        }

        var factory = builder.ServiceProvider.GetRequiredService<IApiVersionSetBuilderFactory>();

        versionedBuilder = new VersionedEndpointRouteBuilder( builder, builder, factory.Create( name ) );
        builder.Finally( FinalizeRoutes );

        return versionedBuilder;
    }

    private static void FinalizeEndpoints( EndpointBuilder endpointBuilder )
    {
        var versionSet = GetApiVersionSet( endpointBuilder.Metadata );
        Finialize( endpointBuilder, versionSet );
    }

    private static void FinalizeRoutes( EndpointBuilder endpointBuilder )
    {
        var versionSet = endpointBuilder.ApplicationServices.GetService<ApiVersionSet>();
        Finialize( endpointBuilder, versionSet );
    }

    private static void Finialize( EndpointBuilder endpointBuilder, ApiVersionSet? versionSet )
    {
        if ( versionSet is null )
        {
            // this should be impossible because WithApiVersionSet had to be called to get here
            endpointBuilder.Metadata.Add( ApiVersionMetadata.Empty );
            return;
        }

        var services = endpointBuilder.ApplicationServices;
        var endpointMetadata = endpointBuilder.Metadata;
        var options = services.GetRequiredService<IOptions<ApiVersioningOptions>>().Value;
        var metadata = Build( endpointMetadata, versionSet, options );
        var reportApiVersions = ReportApiVersions( endpointMetadata ) ||
                                options.ReportApiVersions ||
                                versionSet.ReportApiVersions;

        endpointBuilder.Metadata.Add( metadata );

        var requestDelegate = default( RequestDelegate );

        if ( reportApiVersions )
        {
            requestDelegate = EnsureRequestDelegate( requestDelegate, endpointBuilder.RequestDelegate );
            requestDelegate = new ReportApiVersionsDecorator( requestDelegate, metadata );
            endpointBuilder.RequestDelegate = requestDelegate;
        }

        var parameterSource = endpointBuilder.ApplicationServices.GetRequiredService<IApiVersionParameterSource>();

        if ( parameterSource.VersionsByMediaType() )
        {
            var parameterName = parameterSource.GetParameterName( MediaTypeParameter );

            if ( !string.IsNullOrEmpty( parameterName ) )
            {
                requestDelegate = EnsureRequestDelegate( requestDelegate, endpointBuilder.RequestDelegate );
                requestDelegate = new ContentTypeApiVersionDecorator( requestDelegate, parameterName );
                endpointBuilder.RequestDelegate = requestDelegate;
            }
        }
    }

    private static bool IsApiVersionNeutral( IList<object> metadata )
    {
        var versionNeutral = false;

        for ( var i = metadata.Count - 1; i >= 0; i-- )
        {
            if ( metadata[i] is IApiVersionNeutral )
            {
                versionNeutral = true;
                metadata.RemoveAt( i );
                break;
            }
        }

        if ( versionNeutral )
        {
            for ( var i = metadata.Count - 1; i >= 0; i-- )
            {
                switch ( metadata[i] )
                {
                    case IApiVersionProvider:
                    case IApiVersionNeutral:
                        metadata.RemoveAt( i );
                        break;
                }
            }
        }

        return versionNeutral;
    }

    private static bool ReportApiVersions( IList<object> metadata )
    {
        var result = false;

        for ( var i = metadata.Count - 1; i >= 0; i-- )
        {
            if ( metadata[i] is IReportApiVersions )
            {
                result = true;
                metadata.RemoveAt( i );
            }
        }

        return result;
    }

    private static ApiVersionSet? GetApiVersionSet( IList<object> metadata )
    {
        var result = default( ApiVersionSet );

        for ( var i = metadata.Count - 1; i >= 0; i-- )
        {
            if ( metadata[i] is ApiVersionSet set )
            {
                result ??= set;
                metadata.RemoveAt( i );
            }
        }

        return result;
    }

    private static bool TryGetApiVersions( IList<object> metadata, out ApiVersionBuckets buckets )
    {
        if ( IsApiVersionNeutral( metadata ) )
        {
            buckets = default;
            return false;
        }

        var mapped = default( SortedSet<ApiVersion> );
        var supported = default( SortedSet<ApiVersion> );
        var deprecated = default( SortedSet<ApiVersion> );
        var advertised = default( SortedSet<ApiVersion> );
        var deprecatedAdvertised = default( SortedSet<ApiVersion> );

        for ( var i = metadata.Count - 1; i >= 0; i-- )
        {
            var item = metadata[i];

            if ( item is not IApiVersionProvider provider )
            {
                continue;
            }

            metadata.RemoveAt( i );

            var versions = provider.Versions;
            var target = provider.Options switch
            {
                None => supported ??= new(),
                Mapped => mapped ??= new(),
                Deprecated => deprecated ??= new(),
                Advertised => advertised ??= new(),
                Advertised | Deprecated => deprecatedAdvertised ??= new(),
                _ => default,
            };

            if ( target is null )
            {
                continue;
            }

            for ( var j = 0; j < versions.Count; j++ )
            {
                target.Add( versions[j] );
            }
        }

        buckets = new(
            mapped?.ToArray() ?? Array.Empty<ApiVersion>(),
            supported?.ToArray() ?? Array.Empty<ApiVersion>(),
            deprecated?.ToArray() ?? Array.Empty<ApiVersion>(),
            advertised?.ToArray() ?? Array.Empty<ApiVersion>(),
            deprecatedAdvertised?.ToArray() ?? Array.Empty<ApiVersion>() );

        return true;
    }

    private static ApiVersionMetadata Build( IList<object> metadata, ApiVersionSet versionSet, ApiVersioningOptions options )
    {
        var name = versionSet.Name;
        ApiVersionModel? apiModel;

        if ( !TryGetApiVersions( metadata, out var buckets ) ||
            ( apiModel = versionSet.Build( options ) ).IsApiVersionNeutral )
        {
            if ( string.IsNullOrEmpty( name ) )
            {
                return ApiVersionMetadata.Neutral;
            }

            return new( ApiVersionModel.Neutral, ApiVersionModel.Neutral, name );
        }

        ApiVersionModel endpointModel;
        ApiVersion[] emptyVersions;
        var inheritedSupported = apiModel.SupportedApiVersions;
        var inheritedDeprecated = apiModel.DeprecatedApiVersions;
        var (mapped, supported, deprecated, advertised, advertisedDeprecated) = buckets;
        var isEmpty = mapped.Count == 0 &&
                      supported.Count == 0 &&
                      deprecated.Count == 0 &&
                      advertised.Count == 0 &&
                      advertisedDeprecated.Count == 0;

        if ( isEmpty )
        {
            var noInheritedApiVersions = inheritedSupported.Count == 0 &&
                                         inheritedDeprecated.Count == 0;

            if ( noInheritedApiVersions )
            {
                endpointModel = ApiVersionModel.Empty;
            }
            else
            {
                emptyVersions = Array.Empty<ApiVersion>();
                endpointModel = new(
                    declaredVersions: emptyVersions,
                    inheritedSupported,
                    inheritedDeprecated,
                    emptyVersions,
                    emptyVersions );
            }
        }
        else if ( mapped.Count == 0 )
        {
            endpointModel = new(
                declaredVersions: supported.Union( deprecated ),
                supported.Union( inheritedSupported ),
                deprecated.Union( inheritedDeprecated ),
                advertised,
                advertisedDeprecated );
        }
        else
        {
            emptyVersions = Array.Empty<ApiVersion>();
            endpointModel = new(
                declaredVersions: mapped,
                supportedVersions: inheritedSupported,
                deprecatedVersions: inheritedDeprecated,
                advertisedVersions: emptyVersions,
                deprecatedAdvertisedVersions: emptyVersions );
        }

        return new( apiModel, endpointModel, name );
    }

    private static RequestDelegate EnsureRequestDelegate( RequestDelegate? current, RequestDelegate? original ) =>
        ( current ?? original ) ??
        throw new InvalidOperationException(
            string.Format(
                CultureInfo.CurrentCulture,
                SR.UnsetRequestDelegate,
                nameof( RequestDelegate ),
                nameof( RouteEndpoint ) ) );

    private record struct ApiVersionBuckets(
        IReadOnlyList<ApiVersion> Mapped,
        IReadOnlyList<ApiVersion> Supported,
        IReadOnlyList<ApiVersion> Deprecated,
        IReadOnlyList<ApiVersion> Advertised,
        IReadOnlyList<ApiVersion> AdvertisedDeprecated );
}