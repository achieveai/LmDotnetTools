import express from 'express';
import cors from 'cors';
import WebSocket from 'ws';
import logger from './logger.js';

const app = express();
const PORT = 4000;
const AG_UI_SERVER = 'http://localhost:5264';

app.use(cors());
app.use(express.json());

// Log all incoming requests
app.use((req, res, next) => {
  logger.info('Incoming request', {
    method: req.method,
    path: req.path,
    ip: req.ip
  });
  next();
});

/**
 * Transforms CopilotKit message format to AG-UI format
 */
function transformMessages(copilotKitMessages) {
  return copilotKitMessages
    .filter(msg => msg.textMessage) // Only process text messages
    .map(msg => ({
      role: msg.textMessage.role === 'system' ? 'system' : 'user',
      content: msg.textMessage.content
    }));
}

/**
 * Generates a GraphQL response chunk for SSE
 * @param {object} payload - The payload to send
 * @param {string} path - Optional path for incremental delivery
 */
function generateGraphQLChunk(payload, path = null) {
  const chunk = path
    ? { ...payload, path }
    : payload;
  return `data: ${JSON.stringify(chunk)}\n\n`;
}

/**
 * CopilotKit GraphQL runtime endpoint
 * Handles GraphQL mutations from CopilotKit and bridges to AG-UI protocol
 */
app.post('/copilotkit', async (req, res) => {
  logger.info('Received CopilotKit GraphQL request', {
    operation: req.body?.operationName
  });

  // Set headers for Server-Sent Events (SSE)
  res.setHeader('Content-Type', 'text/event-stream');
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Connection', 'keep-alive');
  res.setHeader('X-Accel-Buffering', 'no'); // Disable nginx buffering

  try {
    // Parse GraphQL request
    const { operationName, variables } = req.body;

    // Handle availableAgents query
    if (operationName === 'availableAgents') {
      res.write(generateGraphQLChunk({
        data: {
          availableAgents: []
        }
      }));
      res.end();
      return;
    }

    if (operationName !== 'generateCopilotResponse') {
      throw new Error(`Unsupported operation: ${operationName}`);
    }

    const { data: requestData } = variables || {};
    if (!requestData) {
      throw new Error('Missing request data in GraphQL variables');
    }

    // Extract messages and transform to AG-UI format
    const copilotKitMessages = requestData.messages || [];
    const agUiMessages = transformMessages(copilotKitMessages);
    const threadId = requestData.threadId || null;
    const runId = requestData.runId || null;
    const agentName = 'InstructionChainAgent';

    logger.info('Processing messages', {
      messageCount: agUiMessages.length,
      threadId,
      runId,
      agentName
    });

    // Call AG-UI CopilotKit endpoint
    const response = await fetch(`${AG_UI_SERVER}/api/copilotkit`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        threadId,
        runId,
        messages: agUiMessages,
        agentName,
      }),
    });

    if (!response.ok) {
      throw new Error(`AG-UI server error: ${response.status}`);
    }

    const agUiResponse = await response.json();
    logger.info('AG-UI session created', {
      sessionId: agUiResponse.sessionId,
      threadId: agUiResponse.threadId,
      runId: agUiResponse.runId
    });

    // Send initial GraphQL response with threadId, runId, and empty messages array
    res.write(generateGraphQLChunk({
      data: {
        generateCopilotResponse: {
          threadId: agUiResponse.threadId,
          runId: agUiResponse.runId,
          extensions: {},
          messages: [], // Initialize empty messages array
          __typename: 'CopilotResponse'
        }
      },
      hasNext: true
    }));

    // Connect to WebSocket
    const wsUrl = agUiResponse.websocketUrl;
    logger.info('Connecting to WebSocket', { wsUrl });

    const ws = new WebSocket(wsUrl);
    let messageId = `msg-${Date.now()}`;
    let messageContent = '';
    let messageStarted = false;

    ws.on('open', () => {
      logger.info('WebSocket connected');
    });

    ws.on('message', (data) => {
      const event = JSON.parse(data.toString());
      logger.debug('AG-UI Event received', {
        eventType: event.type,
        hasContent: !!event.content,
        contentPreview: event.content ? event.content.substring(0, 50) : null
      });

      // Translate AG-UI events to CopilotKit GraphQL format
      switch (event.type) {
        case 'SESSION_STARTED':
          logger.info('Session started', { sessionId: event.sessionId });
          break;

        case 'RUN_STARTED':
          logger.info('Run started');
          messageContent = '';
          messageStarted = false;
          break;

        case 'TEXT_MESSAGE_START':
          if (!messageStarted) {
            messageStarted = true;
            messageId = `msg-${Date.now()}`;
            messageContent = '';

            // Send initial empty message
            res.write(`data: ${JSON.stringify({
              data: {
                generateCopilotResponse: {
                  messages: [{
                    __typename: 'TextMessageOutput',
                    id: messageId,
                    createdAt: new Date().toISOString(),
                    role: 'assistant',
                    content: [],
                    parentMessageId: null
                  }]
                }
              },
              hasNext: true
            })}\n\n`);
          }
          break;

        case 'TEXT_MESSAGE_CONTENT':
          // Stream content updates
          if (event.content) {
            messageContent += event.content;

            logger.debug('Streaming content update', {
              accumulatedLength: messageContent.length,
              contentPreview: messageContent.substring(0, 50) + '...'
            });

            // Send complete message update with accumulated content
            const sseChunk = {
              data: {
                generateCopilotResponse: {
                  messages: [{
                    __typename: 'TextMessageOutput',
                    id: messageId,
                    createdAt: new Date().toISOString(),
                    role: 'assistant',
                    content: [messageContent],
                    parentMessageId: null
                  }]
                }
              },
              hasNext: true
            };

            res.write(`data: ${JSON.stringify(sseChunk)}\n\n`);
          }
          break;

        case 'TEXT_MESSAGE_END':
          // Send final message with status
          res.write(`data: ${JSON.stringify({
            data: {
              generateCopilotResponse: {
                messages: [{
                  __typename: 'TextMessageOutput',
                  id: messageId,
                  createdAt: new Date().toISOString(),
                  role: 'assistant',
                  content: [messageContent],
                  parentMessageId: null,
                  status: {
                    __typename: 'SuccessMessageStatus',
                    code: 'SUCCESS'
                  }
                }]
              }
            },
            hasNext: true
          })}\n\n`);
          break;

        case 'TOOL_CALL_START':
          // Send tool call start
          res.write(generateGraphQLChunk({
            data: {
              generateCopilotResponse: {
                messages: [{
                  __typename: 'ActionExecutionMessageOutput',
                  id: event.toolCallId,
                  createdAt: new Date().toISOString(),
                  name: event.toolName,
                  arguments: '',
                  parentMessageId: messageId,
                  status: {
                    __typename: 'PendingMessageStatus',
                    code: 'PENDING'
                  }
                }]
              }
            }
          }));
          break;

        case 'TOOL_CALL_ARGS':
          // Stream tool arguments
          res.write(generateGraphQLChunk({
            data: {
              generateCopilotResponse: {
                messages: [{
                  __typename: 'ActionExecutionMessageOutput',
                  id: event.toolCallId,
                  arguments: event.arguments
                }]
              }
            }
          }));
          break;

        case 'TOOL_CALL_RESULT':
          // Send tool result
          res.write(generateGraphQLChunk({
            data: {
              generateCopilotResponse: {
                messages: [{
                  __typename: 'ResultMessageOutput',
                  id: `result-${event.toolCallId}`,
                  createdAt: new Date().toISOString(),
                  actionExecutionId: event.toolCallId,
                  actionName: event.toolName,
                  result: event.content
                }]
              }
            }
          }));
          break;

        case 'REASONING_START':
          logger.info('Reasoning started', {
            sessionId: event.sessionId,
            hasEncryptedReasoning: !!event.encryptedReasoning
          });
          // For now, we don't expose reasoning to CopilotKit frontend
          // Could be extended to show reasoning in a special UI component
          break;

        case 'REASONING_MESSAGE_START':
          logger.info('Reasoning message started', {
            messageId: event.messageId,
            visibility: event.visibility
          });
          break;

        case 'REASONING_MESSAGE_CONTENT':
          // Stream reasoning content (could be displayed separately from main response)
          logger.debug('Reasoning content chunk', {
            messageId: event.messageId,
            chunkIndex: event.chunkIndex,
            contentLength: event.content?.length
          });
          break;

        case 'REASONING_MESSAGE_END':
          logger.info('Reasoning message completed', {
            messageId: event.messageId,
            totalChunks: event.totalChunks,
            totalLength: event.totalLength
          });
          break;

        case 'REASONING_END':
          logger.info('Reasoning phase completed', {
            hasSummary: !!event.summary
          });
          break;

        case 'RUN_FINISHED':
          logger.info('Run finished', {
            finalContentLength: messageContent.length
          });

          // Send completion status
          res.write(`data: ${JSON.stringify({
            data: {
              generateCopilotResponse: {
                status: {
                  __typename: 'BaseResponseStatus',
                  code: 'SUCCESS'
                }
              }
            },
            hasNext: false
          })}\n\n`);

          // Close connection
          ws.close();
          res.end();
          break;

        case 'ERROR_EVENT':
          logger.error('Error event received', {
            message: event.message,
            errorCode: event.errorCode
          });

          res.write(generateGraphQLChunk({
            data: {
              generateCopilotResponse: {
                status: {
                  __typename: 'FailedResponseStatus',
                  code: 'ERROR',
                  reason: event.message,
                  details: event.errorCode
                }
              }
            }
          }));

          ws.close();
          res.end();
          break;
      }
    });

    ws.on('error', (error) => {
      logger.error('WebSocket error', {
        error: error.message,
        stack: error.stack
      });

      res.write(generateGraphQLChunk({
        data: {
          generateCopilotResponse: {
            status: {
              __typename: 'FailedResponseStatus',
              code: 'ERROR',
              reason: error.message
            }
          }
        }
      }));

      res.end();
    });

    ws.on('close', () => {
      logger.info('WebSocket closed');
      if (!res.writableEnded) {
        res.end();
      }
    });

    // Timeout after 5 minutes
    setTimeout(() => {
      if (ws.readyState === WebSocket.OPEN) {
        logger.warn('Request timeout', {
          timeoutMs: 300000,
          threadId,
          runId
        });
        ws.close();
        res.end();
      }
    }, 300000);

  } catch (error) {
    logger.error('Request processing error', {
      error: error.message,
      stack: error.stack
    });

    res.write(generateGraphQLChunk({
      errors: [{
        message: error.message,
        extensions: {
          code: 'INTERNAL_SERVER_ERROR'
        }
      }]
    }));

    res.end();
  }
});

app.listen(PORT, () => {
  logger.info('CopilotKit GraphQL runtime bridge started', {
    port: PORT,
    agUiServer: AG_UI_SERVER,
    runtimeUrl: `http://localhost:${PORT}/copilotkit`
  });
});
