import winston from 'winston';
import { SeqTransport } from '@datalust/winston-seq';

// Configuration from environment variables or defaults
const SEQ_URL = process.env.SEQ_URL || 'http://localhost:5341';
const SEQ_API_KEY = process.env.SEQ_API_KEY || 'Uu5XkCVB6llwhmyF7N1e';
const LOG_LEVEL = process.env.LOG_LEVEL || 'info';
const NODE_ENV = process.env.NODE_ENV || 'development';

// Create logger with multiple transports
const logger = winston.createLogger({
  level: LOG_LEVEL,
  format: winston.format.combine(
    winston.format.timestamp({
      format: 'YYYY-MM-DD HH:mm:ss.SSS'
    }),
    winston.format.errors({ stack: true }),
    winston.format.json()
  ),
  defaultMeta: {
    application: 'CopilotKit-Test-Client',
    environment: NODE_ENV
  },
  transports: [
    // Console transport for local development
    new winston.transports.Console({
      format: winston.format.combine(
        winston.format.colorize(),
        winston.format.printf(({ level, message, timestamp, ...meta }) => {
          const metaStr = Object.keys(meta).length > 0 ? JSON.stringify(meta) : '';
          return `${timestamp} [${level}]: ${message} ${metaStr}`;
        })
      )
    }),
    // Seq transport for structured logging
    new SeqTransport({
      serverUrl: SEQ_URL,
      apiKey: SEQ_API_KEY,
      onError: (err) => {
        console.error('Seq logging error:', err);
      },
    })
  ],
});

// Log startup information
logger.info('Logger initialized', {
  seqUrl: SEQ_URL,
  logLevel: LOG_LEVEL,
  environment: NODE_ENV
});

export default logger;
