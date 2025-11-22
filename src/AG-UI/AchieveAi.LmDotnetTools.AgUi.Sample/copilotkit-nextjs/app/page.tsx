"use client";

import { CopilotSidebar } from '@copilotkit/react-ui';
import styles from './page.module.css';

export default function Home() {
  return (
    <div className={styles.container}>
      <header>
        <h1>🤖 CopilotKit + AG-UI Direct Integration</h1>
        <p>CopilotKit consuming AG-UI protocol directly via HttpAgent</p>
        <div className={styles.infoBanner}>
          <p>✅ Using CopilotKit's official UI components</p>
          <p>✅ Direct connection to AG-UI SSE endpoint (no bridge!)</p>
          <p>✅ Server-Sent Events streaming from .NET backend</p>
        </div>
      </header>

      <main>
        <CopilotSidebar
          defaultOpen={true}
          instructions="You are a helpful AI assistant powered by the AG-UI protocol. You have access to various tools including weather, calculator, search, time, and counter. Use these tools to help answer questions accurately."
          labels={{
            title: "AG-UI Assistant",
            initial: "Hello! I'm your AG-UI assistant integrated through CopilotKit. I can help you with:\n\n• Weather information\n• Mathematical calculations\n• Web searches\n• Current time\n• Counter operations\n\nWhat would you like to know?",
          }}
        />

        <div className={styles.content}>
          <div className={styles.welcomeCard}>
            <h2>Welcome to CopilotKit + AG-UI! 🎉</h2>

            <div className={styles.section}>
              <h3>About This Integration</h3>
              <p>This demo showcases a direct integration between:</p>
              <ul>
                <li><strong>CopilotKit Frontend:</strong> Official React UI components</li>
                <li><strong>HttpAgent:</strong> Direct AG-UI protocol consumer from @ag-ui/client</li>
                <li><strong>AG-UI Backend:</strong> .NET server with SSE streaming endpoint</li>
              </ul>
              <p><em>No Node.js bridge required! CopilotKit consumes AG-UI directly.</em></p>
            </div>

            <div className={styles.section}>
              <h3>Try These Prompts</h3>
              <div className={styles.promptsGrid}>
                <div className={styles.promptCard}>
                  <div className={styles.promptIcon}>🌤️</div>
                  <p>"What's the weather in San Francisco?"</p>
                </div>
                <div className={styles.promptCard}>
                  <div className={styles.promptIcon}>🧮</div>
                  <p>"Calculate 15 * 24 + 7"</p>
                </div>
                <div className={styles.promptCard}>
                  <div className={styles.promptIcon}>🕐</div>
                  <p>"What time is it?"</p>
                </div>
                <div className={styles.promptCard}>
                  <div className={styles.promptIcon}>🔍</div>
                  <p>"Search for React documentation"</p>
                </div>
              </div>
            </div>

            <div className={styles.section}>
              <h3>Architecture</h3>
              <div className={styles.architecture}>
                <div className={styles.archStep}>
                  <div className={styles.archNumber}>1</div>
                  <div className={styles.archContent}>
                    <strong>CopilotKit UI</strong>
                    <small>React + @copilotkit/react-ui</small>
                  </div>
                </div>
                <div className={styles.archArrow}>→</div>
                <div className={styles.archStep}>
                  <div className={styles.archNumber}>2</div>
                  <div className={styles.archContent}>
                    <strong>HttpAgent</strong>
                    <small>@ag-ui/client (SSE consumer)</small>
                  </div>
                </div>
                <div className={styles.archArrow}>→</div>
                <div className={styles.archStep}>
                  <div className={styles.archNumber}>3</div>
                  <div className={styles.archContent}>
                    <strong>AG-UI Endpoint</strong>
                    <small>.NET Core (port 5264)</small>
                  </div>
                </div>
              </div>
            </div>

            <div className={styles.section}>
              <h3>Features</h3>
              <div className={styles.featuresGrid}>
                <div className={styles.feature}>
                  <span className={styles.featureIcon}>✅</span>
                  <span>Server-Sent Events (SSE)</span>
                </div>
                <div className={styles.feature}>
                  <span className={styles.featureIcon}>✅</span>
                  <span>Real-time streaming</span>
                </div>
                <div className={styles.feature}>
                  <span className={styles.featureIcon}>✅</span>
                  <span>Tool calling support</span>
                </div>
                <div className={styles.feature}>
                  <span className={styles.featureIcon}>✅</span>
                  <span>Conversation continuity</span>
                </div>
                <div className={styles.feature}>
                  <span className={styles.featureIcon}>✅</span>
                  <span>Direct SSE connection</span>
                </div>
                <div className={styles.feature}>
                  <span className={styles.featureIcon}>✅</span>
                  <span>AG-UI protocol</span>
                </div>
              </div>
            </div>

            <div className={`${styles.section} ${styles.techStack}`}>
              <h3>Tech Stack</h3>
              <div className={styles.stackTags}>
                <span className={styles.tag}>React 18</span>
                <span className={styles.tag}>Next.js 15</span>
                <span className={styles.tag}>CopilotKit 1.3</span>
                <span className={styles.tag}>@ag-ui/client</span>
                <span className={styles.tag}>Server-Sent Events</span>
                <span className={styles.tag}>ASP.NET Core</span>
                <span className={styles.tag}>AG-UI Protocol</span>
              </div>
            </div>
          </div>
        </div>
      </main>
    </div>
  );
}
