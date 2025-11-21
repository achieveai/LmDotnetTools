import { CopilotKit } from '@copilotkit/react-core'
import { CopilotSidebar } from '@copilotkit/react-ui'
import '@copilotkit/react-ui/styles.css'
import './App.css'

function App() {
  return (
    <div className="App">
      <CopilotKit runtimeUrl="http://localhost:4000/copilotkit">
        <div className="container">
          <header>
            <h1>🤖 CopilotKit + AG-UI Integration</h1>
            <p>Real CopilotKit integration using AG-UI protocol</p>
            <div className="info-banner">
              <p>✅ Using CopilotKit's official UI components</p>
              <p>✅ Runtime bridge translating to AG-UI protocol</p>
              <p>✅ WebSocket streaming from .NET backend</p>
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

            <div className="content">
              <div className="welcome-card">
                <h2>Welcome to CopilotKit + AG-UI! 🎉</h2>

                <div className="section">
                  <h3>About This Integration</h3>
                  <p>This demo showcases a real integration between:</p>
                  <ul>
                    <li><strong>CopilotKit Frontend:</strong> Official React UI components</li>
                    <li><strong>Runtime Bridge:</strong> Node.js server translating protocols</li>
                    <li><strong>AG-UI Backend:</strong> .NET server with WebSocket streaming</li>
                  </ul>
                </div>

                <div className="section">
                  <h3>Try These Prompts</h3>
                  <div className="prompts-grid">
                    <div className="prompt-card">
                      <div className="prompt-icon">🌤️</div>
                      <p>"What's the weather in San Francisco?"</p>
                    </div>
                    <div className="prompt-card">
                      <div className="prompt-icon">🧮</div>
                      <p>"Calculate 15 * 24 + 7"</p>
                    </div>
                    <div className="prompt-card">
                      <div className="prompt-icon">🕐</div>
                      <p>"What time is it?"</p>
                    </div>
                    <div className="prompt-card">
                      <div className="prompt-icon">🔍</div>
                      <p>"Search for React documentation"</p>
                    </div>
                  </div>
                </div>

                <div className="section">
                  <h3>Architecture</h3>
                  <div className="architecture">
                    <div className="arch-step">
                      <div className="arch-number">1</div>
                      <div className="arch-content">
                        <strong>CopilotKit UI</strong>
                        <small>React components</small>
                      </div>
                    </div>
                    <div className="arch-arrow">→</div>
                    <div className="arch-step">
                      <div className="arch-number">2</div>
                      <div className="arch-content">
                        <strong>Runtime Bridge</strong>
                        <small>Node.js (port 3001)</small>
                      </div>
                    </div>
                    <div className="arch-arrow">→</div>
                    <div className="arch-step">
                      <div className="arch-number">3</div>
                      <div className="arch-content">
                        <strong>AG-UI Server</strong>
                        <small>.NET (port 5264)</small>
                      </div>
                    </div>
                  </div>
                </div>

                <div className="section">
                  <h3>Features</h3>
                  <div className="features-grid">
                    <div className="feature">
                      <span className="feature-icon">✅</span>
                      <span>Server-Sent Events (SSE)</span>
                    </div>
                    <div className="feature">
                      <span className="feature-icon">✅</span>
                      <span>Real-time streaming</span>
                    </div>
                    <div className="feature">
                      <span className="feature-icon">✅</span>
                      <span>Tool calling support</span>
                    </div>
                    <div className="feature">
                      <span className="feature-icon">✅</span>
                      <span>Conversation continuity</span>
                    </div>
                    <div className="feature">
                      <span className="feature-icon">✅</span>
                      <span>WebSocket backend</span>
                    </div>
                    <div className="feature">
                      <span className="feature-icon">✅</span>
                      <span>AG-UI protocol</span>
                    </div>
                  </div>
                </div>

                <div className="section tech-stack">
                  <h3>Tech Stack</h3>
                  <div className="stack-tags">
                    <span className="tag">React 18</span>
                    <span className="tag">CopilotKit 1.3</span>
                    <span className="tag">Express.js</span>
                    <span className="tag">WebSocket</span>
                    <span className="tag">ASP.NET Core</span>
                    <span className="tag">AG-UI Protocol</span>
                  </div>
                </div>
              </div>
            </div>
          </main>
        </div>
      </CopilotKit>
    </div>
  )
}

export default App
