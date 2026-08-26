using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.E2E.Tests;

internal static class Program
{
    private const string ProjectId = "018f3e78-1234-7abc-8def-0123456789ab";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var runtimeRoot = RequiredArgument(args, "--runtime-root");
            var output = RequiredArgument(args, "--output");
            var result = await VerifyRuntimeAsync(Path.GetFullPath(runtimeRoot)).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(
                output,
                JsonSerializer.Serialize(result, EvidenceJsonOptions)).ConfigureAwait(false);
            Console.WriteLine("WP23 packaged-runtime E2E passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<Wp23RuntimeEvidence> VerifyRuntimeAsync(string runtimeRoot)
    {
        var core = Path.Combine(runtimeRoot, "core", "LLMW.Writing.Core.exe");
        var agentRuntime = Path.Combine(runtimeRoot, "runtime", "LLMW.Writing.AgentRuntime.exe");
        var worker = Path.Combine(runtimeRoot, "worker", "LLMW.Writing.Worker.exe");
        RequireFile(core);
        RequireFile(agentRuntime);
        RequireFile(worker);

        var workingRoot = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP23.E2E", Guid.NewGuid().ToString("N"));
        var appDataRoot = Path.Combine(workingRoot, "application-data");
        Directory.CreateDirectory(workingRoot);
        try
        {
            var migrationWatch = Stopwatch.StartNew();
            var cleanProject = CreateProject(Path.Combine(workingRoot, "clean"));
            migrationWatch.Stop();

            var cold = await OpenAndRunWorkflowAsync(core, cleanProject, appDataRoot, true).ConfigureAwait(false);
            var warm = await OpenAndRunWorkflowAsync(core, cleanProject, appDataRoot, false).ConfigureAwait(false);

            var recoveryProject = CreateProject(Path.Combine(workingRoot, "recovery"));
            SeedRecoverableTransaction(recoveryProject.DatabasePath);
            var recovery = await OpenAndRunWorkflowAsync(core, recoveryProject, appDataRoot, false).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={recoveryProject.DatabasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT status || ':' || project_submission_state FROM authority_transactions WHERE idempotency_key='wp23-recovery';";
                var state = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals("failed:idle", state, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Startup recovery did not converge the pre-commit transaction: " + state);
                }
            }

            var migration = ReadMigrationEvidence(cleanProject.DatabasePath);
            if (!Directory.Exists(appDataRoot))
            {
                throw new InvalidOperationException("Packaged Core did not honor the application data root.");
            }

            return new Wp23RuntimeEvidence(
                SchemaVersion: 1,
                ColdCoreReadyMs: cold.CoreReadyMs,
                WarmCoreReadyMs: warm.CoreReadyMs,
                ProjectOpenMs: warm.ProjectOpenMs,
                RecoveryProjectOpenMs: recovery.ProjectOpenMs,
                RecoveryOverheadMs: Math.Max(0, recovery.ProjectOpenMs - warm.ProjectOpenMs),
                MigrationMs: migrationWatch.Elapsed.TotalMilliseconds,
                UserVersion: migration.UserVersion,
                MigrationCount: migration.MigrationCount,
                BasicWorkflow: "openProject->createWorkflowRun->createRun->createTask",
                BasicWorkflowPassed: true,
                ApplicationDataRootHonored: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingRoot))
            {
                Directory.Delete(workingRoot, recursive: true);
            }
        }
    }

    private static async Task<RunTiming> OpenAndRunWorkflowAsync(
        string corePath,
        ProjectFixture project,
        string appDataRoot,
        bool createWorkflow)
    {
        var workspace = "wp23" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        var runtimeToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = StartCore(corePath, workspace, uiToken, runtimeToken, appDataRoot);
        try
        {
            var readyWatch = Stopwatch.StartNew();
            await using var runtime = await ConnectWithProcessDiagnosticsAsync(
                process, IpcPipeNames.Runtime(workspace), workspace, runtimeToken, IpcClientKind.AgentRuntime, timeout.Token).ConfigureAwait(false);
            await using var ui = await ConnectWithProcessDiagnosticsAsync(
                process, IpcPipeNames.Core(workspace), workspace, uiToken, IpcClientKind.Ui, timeout.Token).ConfigureAwait(false);
            readyWatch.Stop();

            var openWatch = Stopwatch.StartNew();
            var opened = await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(project.Root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token).ConfigureAwait(false);
            openWatch.Stop();
            if (!string.Equals(ProjectId, opened.Payload.ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Packaged Core opened the wrong Project identity.");
            }

            if (createWorkflow)
            {
                var workflow = await runtime.RequestAsync(
                    IpcSemanticTypes.CreateWorkflowRun,
                    new CreateWorkflowRunRequest(null),
                    IpcJsonContext.Default.CreateWorkflowRunRequestEnvelope,
                    IpcJsonContext.Default.CreateWorkflowRunResponseEnvelope,
                    timeout.Token).ConfigureAwait(false);
                var run = await runtime.RequestAsync(
                    IpcSemanticTypes.CreateRun,
                    new CreateRunRequest(workflow.Payload.WorkflowRunId, "writer", null, null),
                    IpcJsonContext.Default.CreateRunRequestEnvelope,
                    IpcJsonContext.Default.CreateRunResponseEnvelope,
                    timeout.Token).ConfigureAwait(false);
                _ = await runtime.RequestAsync(
                    IpcSemanticTypes.CreateTask,
                    new CreateTaskRequest(run.Payload.RunId, "wp23-release-smoke", 0, null, null),
                    IpcJsonContext.Default.CreateTaskRequestEnvelope,
                    IpcJsonContext.Default.CreateTaskResponseEnvelope,
                    timeout.Token).ConfigureAwait(false);
            }

            return new RunTiming(readyWatch.Elapsed.TotalMilliseconds, openWatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            StopProcess(process);
        }
    }

    private static async Task<IpcClientSession> ConnectWithProcessDiagnosticsAsync(
        Process process,
        string pipeName,
        string workspace,
        string token,
        IpcClientKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectAsync(pipeName, workspace, token, kind, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (process.HasExited)
        {
            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Packaged Core exited with code {process.ExitCode}. stdout: {standardOutput} stderr: {standardError}",
                exception);
        }
    }

    private static Process StartCore(string path, string workspace, string uiToken, string runtimeToken, string appDataRoot)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["LLMW_WORKSPACE_INSTANCE_ID"] = workspace;
        info.Environment["LLMW_UI_BOOTSTRAP_TOKEN"] = uiToken;
        info.Environment["LLMW_RUNTIME_BOOTSTRAP_TOKEN"] = runtimeToken;
        info.Environment["LLMW_APPLICATION_DATA_ROOT"] = appDataRoot;
        return Process.Start(info) ?? throw new InvalidOperationException("Packaged Core did not start.");
    }

    private static async Task<IpcClientSession> ConnectAsync(
        string pipeName,
        string workspace,
        string token,
        IpcClientKind kind,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                return await IpcClientSession.HandshakeAsync(
                    pipe, workspace, token, kind, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or IpcProtocolException)
            {
                last = exception;
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("Timed out connecting to packaged Core: " + last);
    }

    private static ProjectFixture CreateProject(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        File.WriteAllText(
            Path.Combine(root, "project.llmw.json"),
            "{\"projectId\":\"" + ProjectId + "\",\"formatVersion\":1,\"schemaVersion\":1}");
        var database = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(database, "wp23-e2e", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new ProjectFixture(root, database);
    }

    private static void SeedRecoverableTransaction(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO authority_transactions(
                transaction_id, transaction_kind, idempotency_key, project_submission_state,
                status, recovery_state, started_at_ms)
            VALUES('018f3e78-1234-7abc-8def-0123456789ac','chapter_accept','wp23-recovery',
                'submitting','submitting','none',1);
            """;
        command.ExecuteNonQuery();
    }

    private static MigrationEvidence ReadMigrationEvidence(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        return new MigrationEvidence(
            Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void StopProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required release payload is missing.", path);
    }

    private static string RequiredArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index == args.Length - 1) throw new ArgumentException("Missing required argument " + name);
        return args[index + 1];
    }

    private sealed record ProjectFixture(string Root, string DatabasePath);
    private sealed record RunTiming(double CoreReadyMs, double ProjectOpenMs);
    private sealed record MigrationEvidence(int UserVersion, int MigrationCount);
    private sealed record Wp23RuntimeEvidence(
        int SchemaVersion,
        double ColdCoreReadyMs,
        double WarmCoreReadyMs,
        double ProjectOpenMs,
        double RecoveryProjectOpenMs,
        double RecoveryOverheadMs,
        double MigrationMs,
        int UserVersion,
        int MigrationCount,
        string BasicWorkflow,
        bool BasicWorkflowPassed,
        bool ApplicationDataRootHonored);
}
