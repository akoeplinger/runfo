using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Models;
using DevOps.Status.Util;
using DevOps.Util;
using DevOps.Util.DotNet;
using DevOps.Util.DotNet.Triage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DevOps.Status.Pages.Search
{
    public class HelixLogsModel : PageModel
    {
        public sealed class HelixLogData
        {
            public int BuildNumber { get; set; }
            public string? Line { get; set; }
            public string? HelixLogKind { get; set; }
            public string? HelixLogUri { get; set; }
        }

        // Results
        public bool DidSearch { get; set; } = false;
        public List<HelixLogData> HelixLogs { get; } = new List<HelixLogData>();
        public string? BuildResultText { get; set; }
        public int BuildStart { get; set; }
        public string? ErrorMessage { get; set; }

        [BindProperty(SupportsGet = true, Name = "bq")]
        public string? BuildQuery { get; set; }
        [BindProperty(SupportsGet = true, Name = "lq")]
        public string? LogQuery { get; set; }

        // Pagination
        [BindProperty(SupportsGet = true, Name = "pageNumber")]
        public int PageNumber { get; set; }
        public PaginationDisplay? PaginationDisplay { get; set; }

        public TriageContextUtil TriageContextUtil { get; }

        public HelixLogsModel(TriageContextUtil triageContextUtil)
        {
            TriageContextUtil = triageContextUtil;
        }

        public async Task OnGet()
        {
            const int pageSize = 50;

            ErrorMessage = null;

            if (string.IsNullOrEmpty(BuildQuery))
            {
                BuildQuery = new SearchBuildsRequest()
                {
                    Definition = "runtime",
                    Started = new DateRequestValue(dayQuery: 3),
                }.GetQueryString();
                return;
            }

            if (!SearchBuildsRequest.TryCreate(BuildQuery, out var buildsRequest, out var errorMessage) ||
                !SearchHelixLogsRequest.TryCreate(LogQuery ?? "", out var logsRequest, out errorMessage))
            {
                ErrorMessage = errorMessage;
                return;
            }

            // Helix logs are only kept for failed builds. If the user doesn't specify a specific result type, 
            // like say cancelled, then just search all non succeeded builds.
            if (buildsRequest.Result is null)
            {
                buildsRequest.Result = new BuildResultRequestValue(BuildResult.Succeeded, EqualsKind.NotEquals);
                BuildQuery = buildsRequest.GetQueryString();
            }

            if (logsRequest.HelixLogKinds.Count == 0)
            {
                logsRequest.HelixLogKinds.Add(HelixLogKind.Console);
                LogQuery = logsRequest.GetQueryString();
            }

            if (string.IsNullOrEmpty(logsRequest.Text))
            {
                ErrorMessage = @"Must specify text to search for 'text: ""StackOverflowException""'";
                return;
            }

            try
            {
                IQueryable<ModelTestResult> query = TriageContextUtil.Context.ModelTestResults.Where(x => x.IsHelixTestResult);
                query = buildsRequest.Filter(query);
                var totalBuildCount = await query.CountAsync();

                var modelResults = await query
                    .Skip(PageNumber * pageSize)
                    .Take(pageSize)
                    .Select(x => new
                    {
                        x.ModelBuild.BuildNumber,
                        x.ModelBuild.AzureOrganization,
                        x.ModelBuild.AzureProject,
                        x.ModelBuild.StartTime,
                        x.ModelBuild.GitHubOrganization,
                        x.ModelBuild.GitHubRepository,
                        x.ModelBuild.GitHubTargetBranch,
                        x.ModelBuild.PullRequestNumber,
                        x.HelixConsoleUri,
                        x.HelixCoreDumpUri,
                        x.HelixRunClientUri,
                        x.HelixTestResultsUri,
                    })
                    .ToListAsync();

                var toQuery = modelResults
                    .Select(x => (
                        new BuildInfo(x.AzureOrganization, x.AzureProject, x.BuildNumber,
                            new GitHubBuildInfo(x.GitHubOrganization, x.GitHubRepository, x.PullRequestNumber, x.GitHubTargetBranch)),
                        new HelixLogInfo(
                            runClientUri: x.HelixRunClientUri,
                            consoleUri: x.HelixConsoleUri,
                            coreDumpUri: x.HelixCoreDumpUri,
                            testResultsUri: x.HelixTestResultsUri)));

                var helixServer = new HelixServer();
                var errorBuilder = new StringBuilder();
                var results = await helixServer.SearchHelixLogsAsync(
                    toQuery,
                    logsRequest,
                    ex => errorBuilder.AppendLine(ex.Message));
                foreach (var result in results)
                {
                    HelixLogs.Add(new HelixLogData()
                    {
                        BuildNumber = result.BuildInfo.Number,
                        Line = result.Line,
                        HelixLogKind = result.HelixLogKind.GetDisplayFileName(),
                        HelixLogUri = result.HelixLogUri,
                    });
                }

                if (errorBuilder.Length > 0)
                {
                    ErrorMessage = errorBuilder.ToString();
                }

                PaginationDisplay = new PaginationDisplay(
                    "/Search/HelixLogs",
                    new Dictionary<string, string>()
                    {
                        { "bq", BuildQuery },
                        { "lq", LogQuery ?? ""},
                    },
                    PageNumber,
                    totalBuildCount / pageSize);
                BuildResultText = $"Results for {PageNumber * pageSize}-{(PageNumber * pageSize) + pageSize} of {totalBuildCount} builds";
                DidSearch = true;
            }
            catch (SqlException ex) when (ex.IsTimeoutViolation())
            {
                ErrorMessage = "Timeout fetching data from the server";
            }
        }
    }
}
