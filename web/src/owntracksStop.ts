/**
 * The OwnTracks config link that switches the app to quiet mode (`{"_type":"configuration","monitoring":-1}`, base64,
 * percent-encoded). Holds no secret. The server has the same literal (`OwnTracksConfigBuilder.StopDeepLink`).
 */
export const STOP_DEEP_LINK = 'owntracks:///config?inline=eyJfdHlwZSI6ImNvbmZpZ3VyYXRpb24iLCJtb25pdG9yaW5nIjotMX0%3D';
