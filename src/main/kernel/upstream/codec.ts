import type { ProviderStreamEvent } from '../../../shared/agent/stream'
import type { UpstreamProtocol } from '../../../shared/domain/provider'
import type { CanonicalRequest } from './canonical'
import { decodeAnthropic } from './decode/anthropic'
import { decodeOpenAIChat } from './decode/openai-chat'
import { decodeOpenAIResponses } from './decode/openai-responses'
import { encodeAnthropic, type EncodedRequest, type UpstreamEncodeOptions } from './encode/anthropic'
import { encodeOpenAIChat } from './encode/openai-chat'
import { encodeOpenAIResponses } from './encode/openai-responses'
import { sseFromResponse, type SseEvent } from './sse'

export function encodeUpstream(
  protocol: UpstreamProtocol,
  request: CanonicalRequest,
  model: string,
  apiKey: string,
  options: UpstreamEncodeOptions
): EncodedRequest {
  switch (protocol) {
    case 'anthropic': return encodeAnthropic(request, model, apiKey, options)
    case 'openai-chat': return encodeOpenAIChat(request, model, apiKey)
    case 'openai-responses': return encodeOpenAIResponses(request, model, apiKey, options)
  }
}

async function* responseEvents(response: Response, signal: AbortSignal): AsyncGenerator<SseEvent> {
  if (response.headers.get('content-type')?.toLowerCase().includes('application/json')) {
    signal.throwIfAborted()
    const data = await response.text()
    signal.throwIfAborted()
    yield { event: 'message', data }
  } else {
    yield* sseFromResponse(response, signal)
  }
}

export function decodeUpstream(
  protocol: UpstreamProtocol,
  response: Response,
  signal: AbortSignal
): AsyncIterable<ProviderStreamEvent> {
  const events = responseEvents(response, signal)
  switch (protocol) {
    case 'anthropic': return decodeAnthropic(events)
    case 'openai-chat': return decodeOpenAIChat(events)
    case 'openai-responses': return decodeOpenAIResponses(events)
  }
}
