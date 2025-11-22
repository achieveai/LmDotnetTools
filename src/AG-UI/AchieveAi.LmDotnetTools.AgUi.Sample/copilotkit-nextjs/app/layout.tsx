import type { Metadata } from "next";
import { CopilotKit } from "@copilotkit/react-core";
import "@copilotkit/react-ui/styles.css";
import "./globals.css";

export const metadata: Metadata = {
  title: "CopilotKit + AG-UI Direct Integration",
  description: "CopilotKit consuming AG-UI protocol directly via HttpAgent",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body>
        <CopilotKit runtimeUrl="/api/copilotkit" agent="default">
          {children}
        </CopilotKit>
      </body>
    </html>
  );
}
