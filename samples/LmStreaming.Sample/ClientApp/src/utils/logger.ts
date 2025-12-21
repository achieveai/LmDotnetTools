/**
 * Client-side logger that batches and sends logs to the server.
 * Provides structured logging with file/line/function context.
 */

export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error';

export interface LogEntry {
  level: LogLevel;
  message: string;
  timestamp: string;
  file?: string;
  line?: number;
  function?: string;
  component?: string;
  data?: unknown;
}

interface LoggerOptions {
  /** Base URL for the logging endpoint */
  baseUrl?: string;
  /** Batch size before auto-flush */
  batchSize?: number;
  /** Auto-flush interval in milliseconds */
  flushInterval?: number;
  /** Minimum log level to send to server */
  minLevel?: LogLevel;
  /** Also log to browser console */
  consoleOutput?: boolean;
}

const LOG_LEVELS: Record<LogLevel, number> = {
  trace: 0,
  debug: 1,
  info: 2,
  warn: 3,
  error: 4,
};

class Logger {
  private buffer: LogEntry[] = [];
  private flushTimer: ReturnType<typeof setTimeout> | null = null;
  private options: Required<LoggerOptions>;
  private isFlushing = false;

  constructor(options: LoggerOptions = {}) {
    this.options = {
      baseUrl: options.baseUrl ?? '',
      batchSize: options.batchSize ?? 10,
      flushInterval: options.flushInterval ?? 5000,
      minLevel: options.minLevel ?? 'debug',
      consoleOutput: options.consoleOutput ?? true,
    };

    // Flush on page unload
    if (typeof window !== 'undefined') {
      window.addEventListener('beforeunload', () => this.flush());
      window.addEventListener('pagehide', () => this.flush());
    }

    this.startFlushTimer();
  }

  private startFlushTimer(): void {
    if (this.flushTimer) {
      clearInterval(this.flushTimer);
    }
    this.flushTimer = setInterval(() => this.flush(), this.options.flushInterval);
  }

  private shouldLog(level: LogLevel): boolean {
    return LOG_LEVELS[level] >= LOG_LEVELS[this.options.minLevel];
  }

  private getCallerInfo(): { file?: string; line?: number; function?: string } {
    try {
      const err = new Error();
      const stack = err.stack?.split('\n');
      if (!stack || stack.length < 5) return {};

      // Skip: Error, getCallerInfo, log method, public method (trace/debug/etc), actual caller
      const callerLine = stack[4];

      // Parse stack trace - format varies by browser
      // Chrome/Edge: "    at functionName (file:line:col)" or "    at file:line:col"
      // Firefox: "functionName@file:line:col" or "@file:line:col"

      let match = callerLine.match(/at\s+(?:(.+?)\s+)?\(?(.+?):(\d+):\d+\)?/);
      if (!match) {
        match = callerLine.match(/(.*)@(.+?):(\d+):\d+/);
      }

      if (match) {
        const [, funcName, filePath, lineNum] = match;
        // Extract just the filename from the path
        const file = filePath?.split('/').pop()?.split('?')[0];
        return {
          function: funcName?.trim() || undefined,
          file,
          line: lineNum ? parseInt(lineNum, 10) : undefined,
        };
      }
    } catch {
      // Ignore stack parsing errors
    }
    return {};
  }

  private log(
    level: LogLevel,
    message: string,
    data?: unknown,
    component?: string
  ): void {
    if (!this.shouldLog(level)) return;

    const callerInfo = this.getCallerInfo();
    const entry: LogEntry = {
      level,
      message,
      timestamp: new Date().toISOString(),
      ...callerInfo,
      component,
      data,
    };

    // Console output
    if (this.options.consoleOutput) {
      const prefix = component ? `[${component}]` : '';
      const consoleMethod = level === 'trace' ? 'debug' : level;
      if (data !== undefined) {
        console[consoleMethod](`${prefix} ${message}`, data);
      } else {
        console[consoleMethod](`${prefix} ${message}`);
      }
    }

    this.buffer.push(entry);

    if (this.buffer.length >= this.options.batchSize) {
      this.flush();
    }
  }

  /**
   * Flush buffered logs to the server
   */
  async flush(): Promise<void> {
    if (this.buffer.length === 0 || this.isFlushing) return;

    const entries = [...this.buffer];
    this.buffer = [];
    this.isFlushing = true;

    try {
      const response = await fetch(`${this.options.baseUrl}/api/logs`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ entries }),
        // Use keepalive for page unload scenarios
        keepalive: true,
      });

      if (!response.ok) {
        // Put entries back if send failed (but don't spam on repeated failures)
        if (this.buffer.length < this.options.batchSize * 2) {
          this.buffer.unshift(...entries);
        }
        console.error('Failed to send logs to server:', response.statusText);
      }
    } catch (err) {
      // Put entries back on network error
      if (this.buffer.length < this.options.batchSize * 2) {
        this.buffer.unshift(...entries);
      }
      console.error('Failed to send logs to server:', err);
    } finally {
      this.isFlushing = false;
    }
  }

  /**
   * Create a child logger with a fixed component name
   */
  forComponent(component: string): ComponentLogger {
    return new ComponentLogger(this, component);
  }

  // Public logging methods
  trace(message: string, data?: unknown): void {
    this.log('trace', message, data);
  }

  debug(message: string, data?: unknown): void {
    this.log('debug', message, data);
  }

  info(message: string, data?: unknown): void {
    this.log('info', message, data);
  }

  warn(message: string, data?: unknown): void {
    this.log('warn', message, data);
  }

  error(message: string, data?: unknown): void {
    this.log('error', message, data);
  }

  // Internal method for ComponentLogger
  _logWithComponent(
    level: LogLevel,
    message: string,
    data: unknown | undefined,
    component: string
  ): void {
    this.log(level, message, data, component);
  }
}

/**
 * Logger instance bound to a specific component
 */
class ComponentLogger {
  constructor(
    private parent: Logger,
    private component: string
  ) {}

  trace(message: string, data?: unknown): void {
    this.parent._logWithComponent('trace', message, data, this.component);
  }

  debug(message: string, data?: unknown): void {
    this.parent._logWithComponent('debug', message, data, this.component);
  }

  info(message: string, data?: unknown): void {
    this.parent._logWithComponent('info', message, data, this.component);
  }

  warn(message: string, data?: unknown): void {
    this.parent._logWithComponent('warn', message, data, this.component);
  }

  error(message: string, data?: unknown): void {
    this.parent._logWithComponent('error', message, data, this.component);
  }
}

// Create singleton instance
export const logger = new Logger({
  batchSize: 10,
  flushInterval: 3000,
  minLevel: 'debug',
  consoleOutput: true,
});

// Export types and classes for advanced usage
export { Logger, ComponentLogger };
export type { LoggerOptions };
