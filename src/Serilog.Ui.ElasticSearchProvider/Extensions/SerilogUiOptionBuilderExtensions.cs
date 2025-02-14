﻿using Elasticsearch.Net;
using Microsoft.Extensions.DependencyInjection;
using Nest;
using Serilog.Ui.Core;
using System;

namespace Serilog.Ui.ElasticSearchProvider
{
    /// <summary>
    ///     ElasticSearch data provider specific extension methods for <see cref="SerilogUiOptionsBuilder"/>.
    /// </summary>
    public static class SerilogUiOptionBuilderExtensions
    {
        /// <summary>
        ///     Configures the SerilogUi to connect to a MongoDB database.
        /// </summary>
        /// <param name="optionsBuilder"> The options builder. </param>
        /// <param name="endpoint"> The url of ElasticSearch server. </param>
        /// <param name="indexName"> Name of the log index. </param>
        /// <exception cref="ArgumentNullException"> throw if endpoint is null </exception>
        /// <exception cref="ArgumentNullException"> throw is indexName is null </exception>
        public static void UseElasticSearchDb(this SerilogUiOptionsBuilder optionsBuilder, Uri endpoint, string indexName)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));

            if (string.IsNullOrEmpty(indexName))
                throw new ArgumentNullException(nameof(indexName));

            var options = new ElasticSearchDbOptions
            {
                IndexName = indexName
            };

            var builder = ((ISerilogUiOptionsBuilder)optionsBuilder);

            builder.Services.AddSingleton(options);

            var pool = new SingleNodeConnectionPool(endpoint);
            var connectionSettings = new ConnectionSettings(pool, sourceSerializer: (builtin, values) => new VanillaSerializer());

            builder.Services.AddSingleton<IElasticClient>(o => new ElasticClient(connectionSettings));
            builder.Services.AddScoped<IDataProvider, ElasticSearchDbDataProvider>();
        }
    }
}