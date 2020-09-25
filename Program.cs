using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Http;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using RestSharp;

using Operation = Microsoft.VisualStudio.Services.WebApi.Patch.Operation;
using ShallowReference = Microsoft.TeamFoundation.Test.WebApi.ShallowReference;

namespace Todo2AzdoTicket
{

        public class StateMappings    {
        public string UserStory { get; set; }
    }

    public class Column    {
        public string id { get; set; }
        public string name { get; set; }
        public int itemLimit { get; set; }
        public StateMappings stateMappings { get; set; }
        public string columnType { get; set; }
        public bool? isSplit { get; set; }
        public string description { get; set; }
    }

    public class Row    {
        public string id { get; set; }
        public string name { get; set; }
    }

    public class Incoming    {
        public List<string> UserStory { get; set; }
    }

    public class InProgress    {
        public List<string> UserStory { get; set; }
    }

    public class Outgoing    {
        public List<string> UserStory { get; set; }
    }

    public class AllowedMappings    {
        public Incoming Incoming { get; set; }
        public InProgress InProgress { get; set; }
        public Outgoing Outgoing { get; set; }
    }

    public class RowField    {
        public string referenceName { get; set; }
    }

    public class DoneField    {
        public string referenceName { get; set; }
        public string url { get; set; }
    }

    public class Fields    {
        public RowField rowField { get; set; }
    }

    public class BoardResponse {
        public Fields fields { get; set; }
    }




    [DataContract]
    public class GhEvent
    {
        [DataMember(Name = "after")] public string After;

        [DataMember(Name = "before")] public string Before;

        [DataMember(Name = "forced")] public bool Forced;

        [DataMember(Name = "pusher")] public Pusher Pusher;

        [DataMember(Name = "repository")] public Repository Repository;
    }

    [DataContract]
    public class SimpleDictionary
    {
        [DataMember]
        public Dictionary<string, string> fields { get; set; }
    }

    [DataContract]
    public class Pusher
    {
        [DataMember(Name = "email")] public string Email;

        [DataMember(Name = "name")] public string Name;
    }

    [DataContract]
    public class Repository
    {
        [DataMember(Name = "full_name")] public string FullName;
    }

    internal class TodoItem
    {
        public readonly string Body;
        public readonly IList<string> Labels;
        private readonly int _line;
        public readonly TodoDiffType DiffType;
        public readonly string File;
        public readonly string Title;

        public TodoItem(string title, int line, string file, int startLines, int endLine, TodoDiffType type,
            string repo, string sha, IList<string> labels)
        {
            Title = title;
            _line = line;
            File = file;
            DiffType = type;
            Labels = labels;
            Body =
                $"<b>{Title}</b> <br>{File}:{_line}<br><br><a href=\"https://github.com/{repo}/blob/{sha}{File}#L{startLines}-L{endLine}\">Visit github</a><br>";
        }

        public string Desription(string author = "")
        {
            var authorLink = string.IsNullOrWhiteSpace(author) ? "" : $"<br><br>Marked by {author}";
            return $"{Body}{authorLink}";
        }

        public override string ToString()
        {
            return $"{Title} @ {File}:{_line} (Labels: {string.Join(", ", Labels)})";
        }
    }

    internal enum TodoDiffType
    {
        None,
        Addition,
        Deletion
    }

    internal static class Program
    {
        private const string ConsoleSeparator = "------------------------------------------";
        private const string ApiBase = @"https://api.github.com/repos/";
        private const string AzdoApiBase = @"https://dev.azure.com/";
        private const string DiffHeaderPattern = @"(?<=diff\s--git\sa.*b.*).+";
        private const string BlockStartPattern = @"((?<=^@@\s).+(?=\s@@))";
        private const string LineNumPattern = @"(?<=\+).+";
        private const string LaneRefSuffix = ".Lane";
        private const string ColumnRefSuffix = ".Column";
        private static IEnumerable<WorkItem> GetActiveItems(Parameters parameters, string[] titles)
        {
            Console.WriteLine(ConsoleSeparator);
            Console.WriteLine("Getting issues");
            var wiql = new Wiql
            {
                Query = "Select [Id], [System.BoardLane] " +
                        "From WorkItems " +
                        "Where [System.Title] In (" + string.Join(",", titles)+")"
            };

            using (var httpClient = new WorkItemTrackingHttpClient(new Uri(AzdoApiBase + parameters.AzDOOrg),
                new VssBasicCredential(string.Empty, parameters.AzDOToken)))
            {
                var result = httpClient.QueryByWiqlAsync(wiql).Result;
                var ids = result.WorkItems.Select(item => item.Id).ToArray();
                if (ids.Length == 0) return Array.Empty<WorkItem>();
                var fields = new[] {"System.Id", "System.Title", "System.State", "System.BoardLane"};
                return httpClient.GetWorkItemsAsync(ids, fields, result.AsOf).Result;
            }
        }

        private static IEnumerable<string> GetDiff(Parameters parameters)
        {
            var client =
                new RestClient($"{ApiBase}{parameters.Repository}/compare/{parameters.OldSha}...{parameters.NewSha}")
                    {Timeout = -1};
            var request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", $"token {parameters.GithubToken}");
            request.AddHeader("Accept", "application/vnd.github.v3.diff");
            var response = client.Execute(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.WriteLine(
                    $"Failed to get diff: {response.Content} - {response.StatusDescription} ({response.StatusCode})");
                Environment.Exit(1);
            }

            return response.Content.Split('\n');
        }

        private static IEnumerable<string> GetLabels(string line, string pattern)
        {
            var labels = new List<string>();
            var labelsMatches = Regex.Matches(line, pattern);
            labels.AddRange(labelsMatches.Select(cap => cap.Value));
            return labels;
        }

        private static IList<TodoItem> GetTodoItems(Parameters parameters, IEnumerable<string> diff)
        {
            var parseLabels = !string.IsNullOrWhiteSpace(parameters.InlineLabelRegex);
            var trimSeparators = parameters.TrimmedCharacters?.Length == 0
                ? new[] {' ', ':', ' ', '"'}
                : parameters.TrimmedCharacters;
            var todos = new List<TodoItem>();
            var lineNumber = 0;
            var currFile = "";

            var excludedPathsCount = parameters.ExcludedPaths?.Length ?? 0;
            var includedPathsCount = parameters.IncludedPaths?.Length ?? 0;

            foreach (var line in diff)
            {
                if (parameters.MaxDiffLineLength > 0 && line.Length > parameters.MaxDiffLineLength)
                {
                    if (!line.StartsWith('-')) lineNumber++;
                    continue;
                }

                var headerMatch = Regex.Match(line, DiffHeaderPattern, RegexOptions.IgnoreCase);
                if (headerMatch.Success)
                {
                    currFile = Regex.Matches(line, @"(?<=)\/.+ b(\/.*)$")[0].Groups[1].Value;
                }
                else if (!string.IsNullOrWhiteSpace(currFile))
                {
                    if (excludedPathsCount > 0
                        && includedPathsCount == 0
                        && parameters.ExcludedPaths.ToList().Any(excl =>
                            currFile.StartsWith(excl, StringComparison.OrdinalIgnoreCase))
                        || excludedPathsCount == 0
                        && includedPathsCount > 0
                        && !parameters.IncludedPaths.ToList().Any(incl =>
                            currFile.StartsWith(incl, StringComparison.OrdinalIgnoreCase))
                        || excludedPathsCount > 0
                        && includedPathsCount > 0
                        && parameters.ExcludedPaths.ToList().Any(excl =>
                            currFile.StartsWith(excl, StringComparison.OrdinalIgnoreCase))
                        && !parameters.IncludedPaths.ToList()
                            .Any(incl => currFile.StartsWith(incl, StringComparison.OrdinalIgnoreCase)))
                    {
                        currFile = "";
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(parameters.FileRegex) &&
                        !Regex.Match(currFile, parameters.FileRegex, RegexOptions.IgnoreCase).Success)
                        continue;

                    var blockStartMatch = Regex.Match(line, BlockStartPattern, RegexOptions.IgnoreCase);
                    if (blockStartMatch.Success)
                    {
                        var lineNumsMatch = Regex.Match(blockStartMatch.Value, LineNumPattern);
                        if (lineNumsMatch.Success) lineNumber = int.Parse(lineNumsMatch.Groups[0].Value.Split(',')[0]);
                    }
                    else
                    {
                        var todoMatch = Regex.Match(line, parameters.TodoRegex);
                        if (todoMatch.Success)
                        {
                            var todoType = LineDiffType(line);
                            if (todoType == TodoDiffType.None) continue;
                            var labels = new List<string> {parameters.LabelToAdd};
                            var title = todoMatch.Value.Trim(trimSeparators);
                            if (parseLabels)
                            {
                                var inlineLabels = GetLabels(line, parameters.InlineLabelRegex);
                                title = Regex.Replace(title, parameters.InlineLabelReplaceRegex, "");
                                labels.AddRange(inlineLabels);
                            }

                            todos.Add(new TodoItem(title.Trim(), lineNumber, currFile,
                                Math.Max(lineNumber - parameters.LinesBefore, 0), lineNumber + parameters.LinesAfter,
                                todoType, parameters.Repository,
                                parameters.NewSha, labels));
                        }

                        if (!line.StartsWith('-')) lineNumber++;
                    }
                }
            }

            return todos;
        }

        private static void CloseItem(Parameters parameters, int? id, string referenceStart)
        {
            var columnReferenceName = referenceStart+ColumnRefSuffix;

            var client =new RestClient(
                    $"{AzdoApiBase}{parameters.AzDOOrg}/{parameters.AzDOProject}/_apis/wit/workitems/{id}?api-version=6.0")
                {
                    Timeout = -1
                };

            var request = new RestRequest(Method.PATCH);
            request.AddHeader("Authorization", $"Basic {parameters.BasicAuthorizationHeaderValue}");
            request.AddParameter("application/json-patch+json",
                string.IsNullOrWhiteSpace(parameters.AzDOClosedColumn)
                    ? $"[\r\n  {{\r\n    \"op\": \"add\",\r\n    \"path\": \"/fields/System.State\",\r\n    \"from\": null,\r\n    \"value\": \"Closed\"\r\n  }}\r\n]"
                    : $"[\r\n  {{\r\n    \"op\": \"add\",\r\n    \"path\": \"/fields/{columnReferenceName}\",\r\n    \"from\": null,\r\n    \"value\": \"{parameters.AzDOClosedColumn}\"\r\n  }}\r\n]",
                ParameterType.RequestBody);
            var response = client.Execute(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.WriteLine($"Failed to close work item {id}: Status code: {response.StatusCode}\n{response.Content}");
                throw new WebException($"Error [{response.StatusCode}] occured while closing item {id}: {response.StatusCode} \n {response.Content}");
            }
            else
            {
                Console.WriteLine("Work item #{0} successfully closed.");
            }
        }

        private static string GetLaneExtensionFieldReferenceName(Parameters parameters)
        {
            var client = new RestClient($"{AzdoApiBase}{parameters.AzDOOrg}/{parameters.AzDOProject}/{parameters.AzDOTeam}/_apis/work/boards/Stories?api-version=6.0");
            client.Timeout = -1;
            var request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", $"Basic {parameters.BasicAuthorizationHeaderValue}");
            var response = client.Execute(request).Content;
            var ser = new DataContractJsonSerializer(typeof(BoardResponse), new DataContractJsonSerializerSettings{ UseSimpleDictionaryFormat = true });
            using var sr = new MemoryStream(Encoding.UTF8.GetBytes(response));
            var board = (BoardResponse) ser.ReadObject(sr);
            return board.fields.rowField.referenceName;
        }

        private static void CreateItem(Parameters parameters, TodoItem todoItem, string referenceStart)
        {
            var uri = new Uri(AzdoApiBase + parameters.AzDOOrg);
            var columnReferenceName = referenceStart+ColumnRefSuffix;
            var laneReferenceName = referenceStart+LaneRefSuffix;
            var credentials = new VssBasicCredential(string.Empty, parameters.AzDOToken);
            var patchDocument = new JsonPatchDocument
            {
                new JsonPatchOperation
                {
                    Operation = Operation.Add, Path = "/fields/System.Title", Value = todoItem.Title
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/Microsoft.VSTS.TCM.ReproSteps",
                    Value = todoItem.Desription(parameters.Author)
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add, Path = "/fields/Microsoft.VSTS.Common.Priority", Value = "1"
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add, Path = "/fields/System.Tags", Value = string.Join(',', todoItem.Labels)
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add, Path = "/fields/System.TeamProject", Value = parameters.AzDOProject
                }
            };
            if (!string.IsNullOrWhiteSpace(parameters.AzDOLane))
            {
                patchDocument.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add, Path = $"/fields/{laneReferenceName}", Value = parameters.AzDOLane
                });
            }
            if (!string.IsNullOrWhiteSpace(parameters.AzDONewColumn))
            {
                patchDocument.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add, Path = $"/fields/{columnReferenceName}", Value = parameters.AzDONewColumn
                });
            }
            var connection = new VssConnection(uri, credentials);
            var workItemTrackingHttpClient = connection.GetClient<WorkItemTrackingHttpClient>();
            try
            {
                var result = workItemTrackingHttpClient.CreateWorkItemAsync(patchDocument, parameters.AzDOProject, "Bug").Result;
                Console.WriteLine("Work item #{0} successfully created: {1}", result.Id, result.Fields["System.Title"]);
            }
            catch (AggregateException ex)
            {
                Console.WriteLine("Error creating work item {0}:  {1}", todoItem.Title ,ex.InnerException.Message);
                throw;
            }
        }

        private static void HandleTodos(Parameters parameters, IList<TodoItem> todos)
        {
            var laneReferenceStart = GetLaneExtensionFieldReferenceName(parameters).Replace(".Lane", "");
            var workItems = GetActiveItems(parameters, todos.Select(todo=>$"'{todo.Title}'").ToArray()).ToList();
            var deletions = todos.Where(t => t.DiffType == TodoDiffType.Deletion).ToList();
            var additions = todos.Where(t => t.DiffType == TodoDiffType.Addition).ToList();
            var caught = false;
            var exceptions = new List<Exception>();
            foreach (var activeIssueId in workItems
                .Where(i => deletions.Select(d => d.Title).Contains(i.Fields["System.Title"]))
                .Select(i => i.Id))
            {
                if (!activeIssueId.HasValue) {
                    continue;
                }

                try
                {
                    CloseItem(parameters, activeIssueId, laneReferenceStart);
                }
                catch (Exception ex)
                {
                    caught = true;
                    exceptions.Add(ex);
                }

                Thread.Sleep(parameters.Timeout);
            }
            foreach (var todoItem in additions) //.Where(todoItem => !workItems.Any(wi => ((string) wi.Fields["System.Title"]).Contains(todoItem.Title)))
            {
                try
                {
                    CreateItem(parameters, todoItem, laneReferenceStart);
                }
                catch (Exception ex)
                {
                    caught = true;
                    exceptions.Add(ex);
                }
                Thread.Sleep(parameters.Timeout);
            }

            if (caught)
            {
                throw new AggregateException("Error(s) occured while handling TODOs.", exceptions);
            }
        }

        private static TodoDiffType LineDiffType(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return TodoDiffType.None;
            return line[0] switch
            {
                '+' => TodoDiffType.Addition,
                '-' => TodoDiffType.Deletion,
                _ => TodoDiffType.None
            };
        }

        private static Parameters ParseParameters()
        {
            var parameters = new Parameters();

            parameters.GithubEventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
            if (!string.IsNullOrWhiteSpace(parameters.GithubEventPath))
            {
                parameters.NoGithubEventData = true;
                var eventData = File.ReadAllText(parameters.GithubEventPath);
                var ser = new DataContractJsonSerializer(typeof(GhEvent));
                using var sr = new MemoryStream(Encoding.UTF8.GetBytes(eventData));
                var githubEvent = (GhEvent) ser.ReadObject(sr);
                parameters.OldSha = githubEvent.Before;
                parameters.NewSha = githubEvent.After;
                parameters.Repository = githubEvent.Repository.FullName;
                parameters.Author = $"{githubEvent.Pusher.Name} <{githubEvent.Pusher.Email}>";
                parameters.Forced = githubEvent.Forced;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_REPOSITORY")))
                parameters.Repository = Environment.GetEnvironmentVariable("INPUT_REPOSITORY");

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_SHA")))
                parameters.NewSha = Environment.GetEnvironmentVariable("INPUT_SHA");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_BASE_SHA")))
                parameters.OldSha = Environment.GetEnvironmentVariable("INPUT_BASE_SHA");

            parameters.GithubToken = Environment.GetEnvironmentVariable("INPUT_TOKEN");
            parameters.AzDOToken = Environment.GetEnvironmentVariable("INPUT_AZDO_TOKEN");
            parameters.AzDOOrg = Environment.GetEnvironmentVariable("INPUT_AZDO_ORGANIZATION");
            parameters.AzDOProject = Environment.GetEnvironmentVariable("INPUT_AZDO_PROJECT");
            parameters.AzDOTeam = Environment.GetEnvironmentVariable("INPUT_AZDO_TEAM");
            parameters.AzDOClosedColumn = Environment.GetEnvironmentVariable("INPUT_AZDO_CLOSED") ?? "Closed";
            parameters.AzDONewColumn = Environment.GetEnvironmentVariable("INPUT_AZDO_NEW_COLUMN") ?? "";
            parameters.AzDOLane = Environment.GetEnvironmentVariable("INPUT_AZDO_LANE") ?? "";
            parameters.TodoRegex = Environment.GetEnvironmentVariable("INPUT_TODO_PATTERN") ?? @"\/\/ TODO";
            parameters.IgnoredRegex = Environment.GetEnvironmentVariable("INPUT_IGNORE_PATH_PATTERN");
            parameters.InlineLabelRegex = Environment.GetEnvironmentVariable("INPUT_LABELS_PATTERN");
            parameters.InlineLabelReplaceRegex = Environment.GetEnvironmentVariable("INPUT_LABELS_REPLACE_PATTERN");
            parameters.LabelToAdd = Environment.GetEnvironmentVariable("INPUT_GH_LABEL");
            parameters.FileRegex = Environment.GetEnvironmentVariable("INPUT_FILE_PATTERN");
            parameters.TrimmedCharacters = Environment.GetEnvironmentVariable("INPUT_TRIM")?.ToCharArray();

            if (!bool.TryParse(Environment.GetEnvironmentVariable("INPUT_NOPUBLISH"), out parameters.NoPublish))
                parameters.NoPublish = false;

            parameters.Timeout =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_TIMEOUT"), out parameters.Timeout)
                    ? 1000
                    : Math.Clamp(parameters.Timeout, 1, 3000);

            parameters.MaxDiffLineLength =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_IGNORED_LINES_LENGTH"),
                    out parameters.MaxDiffLineLength)
                    ? 255
                    : Math.Max(parameters.MaxDiffLineLength, 1);

            parameters.LinesBefore =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_BEFORE"), out parameters.LinesBefore)
                    ? 3
                    : Math.Clamp(parameters.LinesBefore, 0, 15);

            parameters.LinesAfter =
                !int.TryParse(Environment.GetEnvironmentVariable("INPUT_LINES_AFTER"), out parameters.LinesAfter)
                    ? 7
                    : Math.Clamp(parameters.LinesAfter, 0, 15);

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_INCLUDED_PATHS")))
                parameters.IncludedPaths = Environment.GetEnvironmentVariable("INPUT_INCLUDED_PATHS")
                    ?.Split('|', StringSplitOptions.RemoveEmptyEntries);

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INPUT_EXCLUDED_PATHS")))
                parameters.ExcludedPaths = Environment.GetEnvironmentVariable("INPUT_EXCLUDED_PATHS")
                    ?.Split('|', StringSplitOptions.RemoveEmptyEntries);
            return parameters;
        }

        private static void PrintParameters(Parameters parameters)
        {
            Console.WriteLine(ConsoleSeparator);
            Console.WriteLine("Repository:\t{0}", parameters.Repository);
            Console.WriteLine("Old SHA:\t{0}", parameters.OldSha);
            Console.WriteLine("New SHA:\t{0}", parameters.NewSha);
            Console.WriteLine("Token:\t{0}",
                parameters.GithubToken?[0] +
                string.Join("", Enumerable.Repeat('*', parameters.GithubToken?.Length ?? 0 - 2)) +
                parameters.GithubToken?[^1]);
            Console.WriteLine("AzDO Token:\t{0}",
                parameters.GithubToken?[0] +
                string.Join("", Enumerable.Repeat('*', parameters.AzDOToken?.Length ?? 0 - 2)) +
                parameters.GithubToken?[^1]);
            Console.WriteLine("AzDO Org\\Project\\Team:\t{0}", parameters.AzDOOrg + "\\" + parameters.AzDOProject + "\\" + parameters.AzDOTeam);
            Console.WriteLine("AzDO Lane{0}", parameters.AzDOLane);
            Console.WriteLine("AzDO Columns:\tNew:{0}\tClosed{1}", parameters.AzDONewColumn, parameters.AzDOClosedColumn);
            Console.WriteLine("TODO regular expression:\t{0}", parameters.TodoRegex);
            Console.WriteLine("Ignore path regular expression:\t{0}", parameters.IgnoredRegex);
            Console.WriteLine("Inline label regular expression:\t{0}", parameters.InlineLabelRegex);
            Console.WriteLine("Inline label replace regular expression:\t{0}", parameters.InlineLabelReplaceRegex);
            Console.WriteLine("GH Label:\t{0}", parameters.LabelToAdd);
            Console.WriteLine("Trimmed Characters:\t{0}", $"{{{string.Join("", parameters.TrimmedCharacters)}}}");
            Console.WriteLine("Timeout:\t{0}", parameters.Timeout);
            Console.WriteLine("Snippet size:");
            Console.WriteLine("Lines before todo:\t{0}", parameters.LinesBefore);
            Console.WriteLine("Lines after todo:\t{0}", parameters.LinesAfter);
            Console.WriteLine("Maximum processed line length:\t{0}", parameters.MaxDiffLineLength);
            Console.WriteLine("Regex to filter files:\t{0}", parameters.FileRegex);

            var excludedPathsCount = parameters.ExcludedPaths?.Length ?? 0;
            var includedPathsCount = parameters.IncludedPaths?.Length ?? 0;

            Console.WriteLine("List of included paths:");
            for (var i = 0; i < includedPathsCount; i++)
                Console.WriteLine($"{i + 1}:\t{parameters.IncludedPaths[i]}");
            Console.WriteLine("List of excluded paths:");
            for (var i = 0; i < excludedPathsCount; i++)
                Console.WriteLine($"{i + 1}:\t{parameters.ExcludedPaths[i]}");

            if (excludedPathsCount > 0 && includedPathsCount == 0)
                Console.WriteLine(
                    "All found TODOs excluding TODOs in files, which are in a list of excluded paths are handled.");
            else if (excludedPathsCount == 0 && includedPathsCount > 0)
                Console.WriteLine("Only TODOs in files which are under paths in a list of included paths are handled.");
            else if (excludedPathsCount > 0 && includedPathsCount > 0)
                Console.WriteLine(
                    "All found TODOs excluding TODOs in files, which are in a list of excluded paths are handled (list of included paths is a list of exceptions).");
        }

        private static bool CheckParameters(Parameters parameters)
        {
            return !(string.IsNullOrWhiteSpace(parameters.Repository)
                     || string.IsNullOrWhiteSpace(parameters.TodoRegex)
                     || string.IsNullOrWhiteSpace(parameters.OldSha)
                     || string.IsNullOrWhiteSpace(parameters.NewSha)
                     || string.IsNullOrWhiteSpace(parameters.GithubToken)
                     || string.IsNullOrWhiteSpace(parameters.AzDOToken)
                     || string.IsNullOrWhiteSpace(parameters.AzDOOrg)
                     || string.IsNullOrWhiteSpace(parameters.AzDOProject)
                     || string.IsNullOrWhiteSpace(parameters.AzDOClosedColumn));
        }

        private static void PrintTodos(IList<TodoItem> todos)
        {
            Console.WriteLine($"Parsed new TODOs ({todos.Count}):");
            foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Addition))
                Console.WriteLine($"+\t{todoItem}");
            Console.WriteLine("Parsed removed TODOs:");
            foreach (var todoItem in todos.Where(t => t.DiffType == TodoDiffType.Deletion))
                Console.WriteLine($"-\t{todoItem}");
        }

        private static void Main()
        {
            Console.WriteLine("Parsing parameters.");
            var parameters = ParseParameters();
            PrintParameters(parameters);
            Console.WriteLine(ConsoleSeparator);
            if (!CheckParameters(parameters))
            {
                Console.WriteLine("Failed to read some of the mandatory parameters. Aborting.");
                Environment.Exit(1);
            }

            var diff = GetDiff(parameters);
            var todos = GetTodoItems(parameters, diff);
            PrintTodos(todos);
            try
            {
                HandleTodos(parameters, todos);
            }
            catch
            {
                Console.WriteLine(ConsoleSeparator);
                Console.WriteLine("Something went wrong while handling TODOs.");
                Environment.Exit(1);
            }
            Console.WriteLine(ConsoleSeparator);
            Console.WriteLine("Finished updating issues. Thanks for using this tool :)");
        }

        private class Parameters
        {
            public string Author;
            public string AzDOOrg;
            public string AzDOLane;
            public string AzDONewColumn;
            public string AzDOProject;
            public string AzDOTeam;
            public string AzDOClosedColumn;
            public string AzDOToken;
            public string[] ExcludedPaths;
            public string FileRegex;
            public bool Forced;
            public string GithubEventPath;
            public string GithubToken;
            public string IgnoredRegex;
            public string[] IncludedPaths;
            public string InlineLabelRegex;
            public string InlineLabelReplaceRegex;
            public string LabelToAdd;
            public int LinesAfter;
            public int LinesBefore;
            public int MaxDiffLineLength;
            public string NewSha;
            public bool NoGithubEventData;
            public bool NoPublish;
            public string OldSha;
            public string Repository;
            public int Timeout;
            public string TodoRegex;
            public char[] TrimmedCharacters;
            public string BasicAuthorizationHeaderValue {
                get {
                    var plainTextBytes = Encoding.UTF8.GetBytes(":"+AzDOToken);
                    return Convert.ToBase64String(plainTextBytes);
                }
            }
        }
    }
}