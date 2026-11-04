import type { Metadata } from "next";
import { Archivo } from "next/font/google";
import "./globals.css";

const archivo = Archivo({
  subsets: ["latin"],
  weight: ["400", "500", "700", "900"],
});

export const metadata: Metadata = {
  title: "PitchWise",
  description: "Auto-highlights from match recordings",
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en" className={`h-full ${archivo.className}`}>
      <body className="min-h-full bg-[#eceef1] text-[#14181f] antialiased">
        {children}
      </body>
    </html>
  );
}
