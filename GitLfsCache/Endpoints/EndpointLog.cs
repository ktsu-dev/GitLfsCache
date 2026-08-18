// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using Microsoft.Extensions.Logging;

/// <summary>
/// Source-generated log messages for the request handlers.
/// </summary>
internal static partial class EndpointLog
{
	[LoggerMessage(
		EventId = 2000,
		Level = LogLevel.Warning,
		Message = "Request for unknown upstream '{Upstream}' refused.")]
	public static partial void UnknownUpstream(ILogger logger, string upstream);

	[LoggerMessage(
		EventId = 2001,
		Level = LogLevel.Warning,
		Message = "Refused a transfer token for {Oid}: {Reason}")]
	public static partial void RejectedToken(ILogger logger, string oid, string reason);

	[LoggerMessage(
		EventId = 2002,
		Level = LogLevel.Debug,
		Message = "Served {Oid} for {Upstream} from the store.")]
	public static partial void ServedFromCache(ILogger logger, string oid, string upstream);

	[LoggerMessage(
		EventId = 2003,
		Level = LogLevel.Debug,
		Message = "Fetching {Oid} for {Upstream} from upstream and storing it on the way through.")]
	public static partial void FetchingUpstream(ILogger logger, string oid, string upstream);

	[LoggerMessage(
		EventId = 2004,
		Level = LogLevel.Debug,
		Message = "Waiting for another request's fetch of {Oid} for {Upstream}.")]
	public static partial void WaitingForLeader(ILogger logger, string oid, string upstream);

	[LoggerMessage(
		EventId = 2005,
		Level = LogLevel.Information,
		Message = "The fetch of {Oid} this request was waiting on did not finish, so it is fetching upstream itself.")]
	public static partial void LeaderDidNotFinish(ILogger logger, string oid);

	[LoggerMessage(
		EventId = 2006,
		Level = LogLevel.Warning,
		Message = "Upstream returned {StatusCode} for {Oid}, which is relayed to the client unchanged.")]
	public static partial void UpstreamRefusedTransfer(ILogger logger, int statusCode, string oid);

	[LoggerMessage(
		EventId = 2007,
		Level = LogLevel.Warning,
		Message = "Could not store {Oid} while streaming it; the transfer continues and the cache stays cold.")]
	public static partial void StoreSinkFailed(ILogger logger, Exception exception, string oid);

	[LoggerMessage(
		EventId = 2008,
		Level = LogLevel.Debug,
		Message = "Range request for {Oid} missed the store, so it is streamed from upstream without storing.")]
	public static partial void RangeRequestNotStored(ILogger logger, string oid);

	[LoggerMessage(
		EventId = 2009,
		Level = LogLevel.Debug,
		Message = "Relayed {Method} {Path} to upstream {Upstream}.")]
	public static partial void Relayed(ILogger logger, string method, string path, string upstream);

	[LoggerMessage(
		EventId = 2010,
		Level = LogLevel.Warning,
		Message = "A transfer token for action '{TokenAction}' was presented on the {Expected} endpoint.")]
	public static partial void TokenActionMismatch(ILogger logger, string tokenAction, string expected);
}
