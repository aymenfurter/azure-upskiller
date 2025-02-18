using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Microsoft.SemanticKernel.Memory;
using SemanticKernel.Service.CopilotChat.Skills.SortSkill;

namespace Microsoft.SemanticKernel.Connectors.Memory.AzureCognitiveSearchVector
{
    public class AzureSearchMemoryClient
    {
        private static readonly Regex IndexNameSymbolsRegex = new(@"[\s|\\|/|.|_|:]");

        private readonly SearchIndexClient _searchIndexClient;
        private readonly ConcurrentDictionary<string, SearchClient> _clientsByIndex = new();
        private readonly OpenAIClient _openAIClient;

        public AzureSearchMemoryClient(
            string endpoint,
            string apiKey,
            HttpClient? httpClient = null)
        {
            _openAIClient = new OpenAIClient(Environment.GetEnvironmentVariable("OPEN_AI_KEY") ?? string.Empty);

            if (_openAIClient == null)
            {
                AzureKeyCredential cred = new(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? string.Empty);
                var target = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_ENDPOINT") ?? string.Empty;
                _openAIClient = new OpenAIClient(new Uri(target), cred);
            }

            var options = new SearchClientOptions();

            if (httpClient != null)
            {
                options.Transport = new HttpClientTransport(httpClient);
            }

            AzureKeyCredential credentials = new(apiKey);
            _searchIndexClient = new SearchIndexClient(new Uri(endpoint), credentials, options);
        }
        public async IAsyncEnumerable<MemoryQueryResult> SearchAsync(
        string collection,
        string query,
        SortType sortType = SortType.NONE,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var client = GetSearchClient(collection);
            var queryEmbeddings = await GenerateEmbeddings(query, _openAIClient);
            var vector = new SearchQueryVector { KNearestNeighborsCount = 5, Fields = "Vector", Value = queryEmbeddings.ToArray() };

            var options = new SearchOptions
            {
                Vector = vector,
                Size = 10,
                QueryType = SearchQueryType.Semantic,
                QueryLanguage = QueryLanguage.EnUs,
                SemanticConfigurationName = "standard",
                QueryCaption = QueryCaptionType.Extractive,
                QueryAnswer = QueryAnswerType.Extractive,
                Select = { "Text", "Description", "ExternalSourceName", "Id" }
            };


            // print out sortType
            Console.WriteLine($"sortType: {sortType}");

            switch (sortType)
            {
                case SortType.MONTH:
                    DateTime fourWeeksAgo = DateTime.UtcNow.AddDays(-28);
                    options.Filter = $"CreatedAt ge {fourWeeksAgo:o}";
                    options.Size = 150;
                    //options.OrderBy.Add("CreatedAt desc");
                    break;
                
                case SortType.RECENT:
                    DateTime recent = DateTime.UtcNow.AddDays(-28*3);
                    options.Filter = $"CreatedAt ge {recent:o}";
                    options.Size = 150;
                    //options.OrderBy.Add("CreatedAt desc");
                    break;

                case SortType.YEAR:
                    DateTime oneYearAgo = DateTime.UtcNow.AddDays(-365);
                    options.Filter = $"CreatedAt ge {oneYearAgo:o}";
                    //options.OrderBy.Add("CreatedAt desc");
                    break;

                case SortType.NONE:
                    break;

                default:
                    throw new ArgumentException($"Unknown sort type: {sortType}");
            }



            Response<SearchResults<AzureSearchMemoryRecord>>? searchResult = null;
            try
            {
                searchResult = await client
                    .SearchAsync<AzureSearchMemoryRecord>(query, options, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
            }

            if (searchResult != null)
            {
                if (searchResult.Value.TotalCount <= 10) {
                    await foreach (SearchResult<AzureSearchMemoryRecord>? doc in searchResult.Value.GetResultsAsync())
                    {
                        yield return new MemoryQueryResult(ToMemoryRecordMetadata(doc.Document), doc.RerankerScore ?? 1, null);
                    }
                } else {
                    List<SearchResult<AzureSearchMemoryRecord>> allResults = new List<SearchResult<AzureSearchMemoryRecord>>();

                    await foreach (SearchResult<AzureSearchMemoryRecord>? doc in searchResult.Value.GetResultsAsync())
                    {
                        allResults.Add(doc);
                    }

                    var sortedResults = allResults.OrderByDescending(r => r.Document.CreatedAt).Take(10);

                    foreach (var doc in sortedResults)
                    {
                        yield return new MemoryQueryResult(ToMemoryRecordMetadata(doc.Document), doc.RerankerScore ?? 1, null);
                    }
                }
            }
        }


        private static async Task<IReadOnlyList<float>> GenerateEmbeddings(string text, OpenAIClient openAIClient)
        {
            var response = await openAIClient.GetEmbeddingsAsync("text-embedding-ada-002", new EmbeddingsOptions(text));
            return response.Value.Data[0].Embedding;
        }

        private SearchClient GetSearchClient(string indexName)
        {
            if (!_clientsByIndex.TryGetValue(indexName, out SearchClient client))
            {
                client = _searchIndexClient.GetSearchClient(indexName);
                _clientsByIndex[indexName] = client;
            }

            return client;
        }

        private static MemoryRecordMetadata ToMemoryRecordMetadata(AzureSearchMemoryRecord data)
        {
            return new MemoryRecordMetadata(
                isReference: data.IsReference,
                id: data.Id,
                text: data.Text ?? string.Empty,
                description: data.Description ?? string.Empty,
                externalSourceName: data.ExternalSourceName,
                additionalMetadata: data.AdditionalMetadata ?? string.Empty);
        }
    }
}
