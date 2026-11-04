"use client";

import { useEffect, useRef, useState } from "react";

export interface PlayerDot {
  track_id: number | null;
  cls: string;
  pitch_x: number;
  pitch_y: number;
}

interface CalibrationPoint {
  pixel: [number, number];
  pitch: [number, number];
}

interface Props {
  players: PlayerDot[];
  calibrated: boolean;
  onCalibrate: (points: CalibrationPoint[]) => void;
  frameSnapshotUrl?: string | null;
}

const PITCH_W = 420;
const PITCH_H = 280;
const REAL_W = 105;
const REAL_H = 68;

const KNOWN_PITCH_POINTS: { label: string; coords: [number, number] }[] = [
  { label: "Center circle", coords: [52.5, 34] },
  { label: "Left penalty spot", coords: [11, 34] },
  { label: "Right penalty spot", coords: [94, 34] },
  { label: "Top-left corner", coords: [0, 0] },
  { label: "Top-right corner", coords: [105, 0] },
  { label: "Bottom-left corner", coords: [0, 68] },
  { label: "Bottom-right corner", coords: [105, 68] },
  { label: "Left penalty box (top)", coords: [0, 13.84] },
  { label: "Left penalty box (bottom)", coords: [0, 54.16] },
  { label: "Right penalty box (top)", coords: [105, 13.84] },
  { label: "Right penalty box (bottom)", coords: [105, 54.16] },
];

function drawPitch(ctx: CanvasRenderingContext2D) {
  const s = (x: number) => (x / REAL_W) * PITCH_W;
  const t = (y: number) => (y / REAL_H) * PITCH_H;

  ctx.fillStyle = "#2d7a2d";
  ctx.fillRect(0, 0, PITCH_W, PITCH_H);

  // Stripes
  for (let i = 0; i < 7; i++) {
    if (i % 2 === 0) {
      ctx.fillStyle = "rgba(0,0,0,0.06)";
      ctx.fillRect(i * (PITCH_W / 7), 0, PITCH_W / 7, PITCH_H);
    }
  }

  ctx.strokeStyle = "rgba(255,255,255,0.85)";
  ctx.lineWidth = 1.5;

  // Outline
  ctx.strokeRect(2, 2, PITCH_W - 4, PITCH_H - 4);

  // Halfway line
  ctx.beginPath();
  ctx.moveTo(PITCH_W / 2, 2);
  ctx.lineTo(PITCH_W / 2, PITCH_H - 2);
  ctx.stroke();

  // Center circle
  ctx.beginPath();
  ctx.arc(PITCH_W / 2, PITCH_H / 2, s(9.15), 0, Math.PI * 2);
  ctx.stroke();

  // Center spot
  ctx.fillStyle = "rgba(255,255,255,0.85)";
  ctx.beginPath();
  ctx.arc(PITCH_W / 2, PITCH_H / 2, 2.5, 0, Math.PI * 2);
  ctx.fill();

  // Left penalty box
  ctx.strokeRect(s(0), t(13.84), s(16.5), t(40.32));
  // Left goal box
  ctx.strokeRect(s(0), t(24.84), s(5.5), t(18.32));

  // Right penalty box
  ctx.strokeRect(s(88.5), t(13.84), s(16.5), t(40.32));
  // Right goal box
  ctx.strokeRect(s(99.5), t(24.84), s(5.5), t(18.32));

  // Penalty spots
  ctx.fillStyle = "rgba(255,255,255,0.85)";
  ctx.beginPath();
  ctx.arc(s(11), t(34), 2.5, 0, Math.PI * 2);
  ctx.fill();
  ctx.beginPath();
  ctx.arc(s(94), t(34), 2.5, 0, Math.PI * 2);
  ctx.fill();
}

export function PitchMinimap({ players, calibrated, onCalibrate, frameSnapshotUrl }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const prevPlayersRef = useRef<PlayerDot[]>([]);
  const [showCalibModal, setShowCalibModal] = useState(false);
  const [calibPixelPoints, setCalibPixelPoints] = useState<[number, number][]>([]);
  const [calibPitchPoints, setCalibPitchPoints] = useState<[number, number][]>([]);
  const [pendingPitch, setPendingPitch] = useState<[number, number] | null>(null);
  const imgRef = useRef<HTMLImageElement | null>(null);

  // Lerp toward new positions
  useEffect(() => {
    const lerped = players.map((p) => {
      const prev = prevPlayersRef.current.find((pp) => pp.track_id === p.track_id);
      if (!prev) return p;
      return {
        ...p,
        pitch_x: prev.pitch_x + (p.pitch_x - prev.pitch_x) * 0.4,
        pitch_y: prev.pitch_y + (p.pitch_y - prev.pitch_y) * 0.4,
      };
    });
    prevPlayersRef.current = lerped;

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    drawPitch(ctx);

    for (const p of lerped) {
      const isBall = p.cls === "ball";
      const cx = (p.pitch_x / REAL_W) * PITCH_W;
      const cy = (p.pitch_y / REAL_H) * PITCH_H;

      if (cx < 0 || cx > PITCH_W || cy < 0 || cy > PITCH_H) continue;

      ctx.beginPath();
      ctx.arc(cx, cy, isBall ? 5 : 6, 0, Math.PI * 2);
      ctx.fillStyle = isBall ? "#facc15" : "#3b82f6";
      ctx.fill();
      ctx.strokeStyle = "white";
      ctx.lineWidth = 1.5;
      ctx.stroke();

      if (!isBall && p.track_id !== null) {
        ctx.fillStyle = "white";
        ctx.font = "bold 9px sans-serif";
        ctx.textAlign = "center";
        ctx.fillText(String(p.track_id), cx, cy + 3.5);
      }
    }
  }, [players]);

  // Initial pitch draw
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    drawPitch(ctx);
  }, []);

  function handleCalibImageClick(e: React.MouseEvent<HTMLImageElement>) {
    if (!pendingPitch) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    const scaleX = (e.currentTarget.naturalWidth || 1280) / rect.width;
    const scaleY = (e.currentTarget.naturalHeight || 720) / rect.height;

    setCalibPixelPoints((prev) => [...prev, [x * scaleX, y * scaleY]]);
    setCalibPitchPoints((prev) => [...prev, pendingPitch]);
    setPendingPitch(null);
  }

  function handleSubmitCalib() {
    const points = calibPixelPoints.map((px, i) => ({
      pixel: px,
      pitch: calibPitchPoints[i],
    }));
    onCalibrate(points);
    setShowCalibModal(false);
    setCalibPixelPoints([]);
    setCalibPitchPoints([]);
    setPendingPitch(null);
  }

  return (
    <div className="flex flex-col bg-white rounded-xl border border-[#eaecf0] shadow-sm overflow-hidden">
      <div className="px-4 py-3 border-b border-[#eaecf0] flex items-center justify-between">
        <div>
          <h3 className="text-[14px] font-bold text-[#14181f]">Pitch View</h3>
          <p className="text-[11px] text-[#9aa0a8] mt-0.5">
            {calibrated ? "Calibrated — real coordinates" : "Approximate (pixel-based)"}
          </p>
        </div>
        <button
          onClick={() => setShowCalibModal(true)}
          className="text-[12px] font-semibold text-[#2f5fe0] border border-[#2f5fe0] rounded-lg px-3 py-1.5 hover:bg-[#eef1ff] transition-colors"
        >
          Calibrate
        </button>
      </div>

      <div className="p-3 flex justify-center">
        <canvas
          ref={canvasRef}
          width={PITCH_W}
          height={PITCH_H}
          className="rounded-lg"
          style={{ maxWidth: "100%" }}
        />
      </div>

      <div className="px-4 pb-3 flex gap-4 text-[11px] text-[#6b7280]">
        <span className="flex items-center gap-1">
          <span className="w-2.5 h-2.5 rounded-full bg-blue-500 inline-block" /> Players
        </span>
        <span className="flex items-center gap-1">
          <span className="w-2.5 h-2.5 rounded-full bg-yellow-400 inline-block" /> Ball
        </span>
        <span className="ml-auto font-medium">{players.length} detected</span>
      </div>

      {showCalibModal && (
        <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-2xl shadow-2xl w-full max-w-2xl overflow-hidden">
            <div className="px-6 py-4 border-b border-[#eaecf0] flex items-center justify-between">
              <h2 className="text-[16px] font-bold text-[#14181f]">Calibrate Pitch View</h2>
              <button
                onClick={() => setShowCalibModal(false)}
                className="text-[#9aa0a8] hover:text-[#14181f] text-xl leading-none"
              >
                ×
              </button>
            </div>

            <div className="p-6 space-y-4">
              <p className="text-[13px] text-[#6b7280]">
                Select a known pitch point from the dropdown, then click its location on the video frame.
                Add at least 4 pairs, then click Submit.
              </p>

              <div className="flex gap-3">
                <select
                  value={pendingPitch ? JSON.stringify(pendingPitch) : ""}
                  onChange={(e) => {
                    if (e.target.value) {
                      const pt = JSON.parse(e.target.value) as [number, number];
                      setPendingPitch(pt);
                    }
                  }}
                  className="flex-1 border border-[#e4e7ec] rounded-lg px-3 py-2 text-[13px] focus:outline-none focus:border-[#2f5fe0]"
                >
                  <option value="">— Select pitch point —</option>
                  {KNOWN_PITCH_POINTS.map((p) => (
                    <option key={p.label} value={JSON.stringify(p.coords)}>
                      {p.label} ({p.coords[0]},{p.coords[1]})
                    </option>
                  ))}
                </select>
              </div>

              {frameSnapshotUrl ? (
                <div className="relative border border-[#e4e7ec] rounded-lg overflow-hidden">
                  <img
                    src={frameSnapshotUrl}
                    alt="Video frame"
                    className={`w-full cursor-${pendingPitch ? "crosshair" : "default"}`}
                    onClick={handleCalibImageClick}
                  />
                  {calibPixelPoints.map((pt, i) => (
                    <div
                      key={i}
                      className="absolute w-3 h-3 -translate-x-1.5 -translate-y-1.5 rounded-full bg-yellow-400 border-2 border-white shadow"
                      style={{ left: pt[0] + "px", top: pt[1] + "px" }}
                    />
                  ))}
                </div>
              ) : (
                <div className="bg-[#f8f9fb] rounded-lg p-4 text-center text-[13px] text-[#9aa0a8]">
                  No video frame available. Start the stream first.
                </div>
              )}

              <p className="text-[12px] text-[#9aa0a8]">
                {calibPixelPoints.length} point{calibPixelPoints.length !== 1 ? "s" : ""} added
                {calibPixelPoints.length > 0 && ` (need at least 4)`}
              </p>
            </div>

            <div className="px-6 pb-5 flex justify-end gap-3">
              <button
                onClick={() => { setCalibPixelPoints([]); setCalibPitchPoints([]); setPendingPitch(null); }}
                className="text-[13px] font-medium text-[#6b7280] hover:text-[#14181f] px-4 py-2"
              >
                Clear
              </button>
              <button
                disabled={calibPixelPoints.length < 4}
                onClick={handleSubmitCalib}
                className="bg-[#2f5fe0] hover:bg-[#2451c7] disabled:opacity-40 text-white text-[13px] font-semibold rounded-lg px-5 py-2 transition-colors"
              >
                Submit calibration
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
