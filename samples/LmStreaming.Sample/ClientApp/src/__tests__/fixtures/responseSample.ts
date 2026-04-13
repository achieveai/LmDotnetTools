/**
 * Test fixture containing the raw SSE stream from ResponseSample.md
 * This simulates a real response with reasoning_update, reasoning, text_update messages and done event
 *
 * Key characteristics:
 * - All messages have messageOrderIdx and chunkIdx fields
 * - reasoning_update messages have messageOrderIdx=0, chunkIdx incrementing
 * - reasoning message has messageOrderIdx=1
 * - text_update messages have messageOrderIdx=2, chunkIdx incrementing
 * - Text updates are DELTA chunks that should be concatenated (not replaced)
 * - Reasoning updates are also DELTA chunks that should be concatenated
 */
export const responseSampleRaw = `data: {"$type":"reasoning_update","reasoning":"<|user_pre|><|reasoning|> Hi there\\nReason:","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":0}

data: {"$type":"reasoning_update","reasoning":"How can that","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":1}

data: {"$type":"reasoning_update","reasoning":"be?The user says","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":2}

data: {"$type":"reasoning_update","reasoning":": \\"Setup ","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":3}

data: {"$type":"reasoning_update","reasoning":"message 1 ","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":4}

data: {"$type":"reasoning_update","reasoning":"for existing conversation","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":5}

data: {"$type":"reasoning_update","reasoning":" test\\" then","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":6}

data: {"$type":"reasoning_update","reasoning":" \\"Setup message","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":7}

data: {"$type":"reasoning_update","reasoning":" 2 for","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":8}

data: {"$type":"reasoning_update","reasoning":" existing conversation\\"","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":9}

data: {"$type":"reasoning_update","reasoning":". They likely","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":10}

data: {"$type":"reasoning_update","reasoning":" want me","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":11}

data: {"$type":"reasoning_update","reasoning":"to  the","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":12}

data: {"$type":"reasoning_update","reasoning":"existing conversation ","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":13}

data: {"$type":"reasoning_update","reasoning":"after refresh \\".","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":14}

data: {"$type":"reasoning_update","reasoning":" They likely","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":15}

data: {"$type":"reasoning_update","reasoning":"want  me","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":16}

data: {"$type":"reasoning_update","reasoning":"to respond ","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":17}

data: {"$type":"reasoning_update","reasoning":"to a previous","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":18}

data: {"$type":"reasoning_update","reasoning":" conversation. But","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":19}

data: {"$type":"reasoning_update","reasoning":" we don't","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":20}

data: {"$type":"reasoning_update","reasoning":"have  the","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":21}

data: {"$type":"reasoning_update","reasoning":"previous conversation ","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":22}

data: {"$type":"reasoning_update","reasoning":"context.<|user_post|><|reasoning|> Hi there\\nReason:","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":23}

data: {"$type":"reasoning_update","reasoning":"How can that","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":24}

data: {"$type":"reasoning_update","reasoning":"be?","isUpdate":true,"visibility":0,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":0,"chunkIdx":25}

data: {"$type":"reasoning","reasoning":"PHx1c2VyX3ByZXw+PHxyZWFzb25pbmd8PiBIaSB0aGVyZQpSZWFzb246IEhvdyBjYW4gdGhhdCBiZT9UaGUgdXNlciBzYXlzIDogIlNldHVwICBtZXNzYWdlIDEgIGZvciBleGlzdGluZyBjb252ZXJzYXRpb24gIHRlc3QiIHRoZW4gICJTZXR1cCBtZXNzYWdlICAyIGZvciAgZXhpc3RpbmcgY29udmVyc2F0aW9uIiAuIFRoZXkgbGlrZWx5ICB3YW50IG1lIHRvICB0aGUgZXhpc3RpbmcgY29udmVyc2F0aW9uICBhZnRlciByZWZyZXNoICIuICBUaGV5IGxpa2VseSB3YW50ICBtZSB0byByZXNwb25kICB0byBhIHByZXZpb3VzICBjb252ZXJzYXRpb24uIEJ1dCAgd2UgZG9uJ3QgaGF2ZSAgdGhlIHByZXZpb3VzIGNvbnZlcnNhdGlvbiAgY29udGV4dC48fHVzZXJfcG9zdHw+PHxyZWFzb25pbmd8PiBIaSB0aGVyZQpSZWFzb246IEhvdyBjYW4gdGhhdCBiZT8=","visibility":2,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":1}

data: {"$type":"text_update","text":"<|user_pre|><|text_message|> Hi there\\nReason:","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":0,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"How can that","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":1,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"be?lorem ipsum dolor","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":2,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"sit amet consectetur","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":3,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"adipiscing elit sed","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":4,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"do eiusmod tempor","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":5,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"incididunt ut labore","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":6,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"et dolore magna","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":7,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"aliqua ut enim","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":8,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ad minim veniam","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":9,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"quis nostrud exercitation","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":10,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ullamco laboris nisi","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":11,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ut aliquip ex","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":12,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ea commodo consequat","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":13,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"duis aute irure","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":14,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"dolor in reprehenderit","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":15,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"in voluptate velit","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":16,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"esse cillum dolore","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":17,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"eu fugiat nulla","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":18,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"pariatur excepteur sint","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":19,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"occaecat cupidatat non","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":20,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"proident sunt in","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":21,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"culpa qui officia","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":22,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"deserunt mollit anim","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":23,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"id est laborum","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":24,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"lorem ipsum dolor","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":25,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"sit amet consectetur","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":26,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"adipiscing elit sed","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":27,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"do eiusmod tempor","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":28,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"incididunt ut labore","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":29,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"et dolore magna","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":30,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"aliqua ut enim","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":31,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ad minim veniam","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":32,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"quis nostrud exercitation","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":33,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ullamco laboris nisi","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":34,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ut aliquip ex","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":35,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"ea commodo<|user_post|><|text_message|> Hi","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":36,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"there\\nReason: How can","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":37,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

data: {"$type":"text_update","text":"that be?","isUpdate":true,"isThinking":false,"role":"assistant","generationId":"gen-1766214628-6598974a3036412c","messageOrderIdx":2,"chunkIdx":38,"is_streaming":true,"completion_id":"gen-1766214628-6598974a3036412c"}

event: done
data: {}
`;

/**
 * Split the raw SSE stream into individual events
 */
export function splitSSEEvents(raw: string): string[] {
  // Split by double newlines (SSE event separator)
  return raw
    .split('\n\n')
    .map((chunk) => chunk.trim())
    .filter((chunk) => chunk.length > 0);
}

/**
 * Expected generation ID used in all messages
 */
export const expectedGenerationId = 'gen-1766214628-6598974a3036412c';

/**
 * Count of different message types in the sample
 */
export const messageCounts = {
  reasoningUpdate: 26, // All reasoning_update messages (chunkIdx 0-25)
  reasoning: 1, // Final reasoning message
  textUpdate: 39, // All text_update messages (chunkIdx 0-38)
  done: 1, // Done event
};

/**
 * Expected final text from all text_update messages concatenated
 * This is the full accumulated text from all 39 text_update chunks
 */
export const expectedFinalText =
  '<|user_pre|><|text_message|> Hi there\nReason:' +
  'How can that' +
  'be?lorem ipsum dolor' +
  'sit amet consectetur' +
  'adipiscing elit sed' +
  'do eiusmod tempor' +
  'incididunt ut labore' +
  'et dolore magna' +
  'aliqua ut enim' +
  'ad minim veniam' +
  'quis nostrud exercitation' +
  'ullamco laboris nisi' +
  'ut aliquip ex' +
  'ea commodo consequat' +
  'duis aute irure' +
  'dolor in reprehenderit' +
  'in voluptate velit' +
  'esse cillum dolore' +
  'eu fugiat nulla' +
  'pariatur excepteur sint' +
  'occaecat cupidatat non' +
  'proident sunt in' +
  'culpa qui officia' +
  'deserunt mollit anim' +
  'id est laborum' +
  'lorem ipsum dolor' +
  'sit amet consectetur' +
  'adipiscing elit sed' +
  'do eiusmod tempor' +
  'incididunt ut labore' +
  'et dolore magna' +
  'aliqua ut enim' +
  'ad minim veniam' +
  'quis nostrud exercitation' +
  'ullamco laboris nisi' +
  'ut aliquip ex' +
  'ea commodo<|user_post|><|text_message|> Hi' +
  'there\nReason: How can' +
  'that be?';

/**
 * The final reasoning message contains base64 encoded content
 */
export const expectedReasoningBase64 =
  'PHx1c2VyX3ByZXw+PHxyZWFzb25pbmd8PiBIaSB0aGVyZQpSZWFzb246IEhvdyBjYW4gdGhhdCBiZT9UaGUgdXNlciBzYXlzIDogIlNldHVwICBtZXNzYWdlIDEgIGZvciBleGlzdGluZyBjb252ZXJzYXRpb24gIHRlc3QiIHRoZW4gICJTZXR1cCBtZXNzYWdlICAyIGZvciAgZXhpc3RpbmcgY29udmVyc2F0aW9uIiAuIFRoZXkgbGlrZWx5ICB3YW50IG1lIHRvICB0aGUgZXhpc3RpbmcgY29udmVyc2F0aW9uICBhZnRlciByZWZyZXNoICIuICBUaGV5IGxpa2VseSB3YW50ICBtZSB0byByZXNwb25kICB0byBhIHByZXZpb3VzICBjb252ZXJzYXRpb24uIEJ1dCAgd2UgZG9uJ3QgaGF2ZSAgdGhlIHByZXZpb3VzIGNvbnZlcnNhdGlvbiAgY29udGV4dC48fHVzZXJfcG9zdHw+PHxyZWFzb25pbmd8PiBIaSB0aGVyZQpSZWFzb246IEhvdyBjYW4gdGhhdCBiZT8=';
