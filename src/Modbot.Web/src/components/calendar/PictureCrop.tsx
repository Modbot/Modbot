import { useEffect, useRef } from 'react'
import { cropSize, CROP_SMALLEST, moveCrop, sizeCrop, type CropBox } from '@/lib/eventPicture'
import { drawCrop, type OpenPicture } from '@/lib/pictureFiles'

/**
 * A crop box in a fixed shape over a picture (calendar design §15.3): drag the box to move it, and
 * the slider makes it larger or smaller around the same middle. Everything outside the box is
 * dimmed. The box is kept in the picture's own pixels, so how large the picture is drawn here
 * changes nothing.
 */
export function PictureCrop({
  picture,
  aspect,
  box,
  onBox,
  label,
}: {
  picture: OpenPicture
  /** Width ÷ height of the box. */
  aspect: number
  box: CropBox
  onBox: (box: CropBox) => void
  /** Names the box for a screen reader and the slider. */
  label: string
}) {
  const frame = useRef<HTMLDivElement>(null)
  const drag = useRef<{ x: number; y: number; scale: number; box: CropBox } | null>(null)
  const { width, height } = picture

  const percent = (value: number, of: number) => `${(value / of) * 100}%`

  return (
    <div className="flex flex-col gap-2">
      <div
        ref={frame}
        className="relative w-full max-w-md self-start overflow-hidden rounded-sm border border-(length:--hairline) bg-muted select-none"
        style={{ aspectRatio: `${width} / ${height}` }}
      >
        <img src={picture.url} alt="" draggable={false} className="pointer-events-none absolute inset-0 size-full" />
        <div
          role="slider"
          tabIndex={0}
          aria-label={label}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round((box.x / Math.max(1, width - box.width)) * 100) || 0}
          className="absolute cursor-move touch-none border-2 border-white outline-none focus-visible:border-primary"
          style={{
            left: percent(box.x, width),
            top: percent(box.y, height),
            width: percent(box.width, width),
            height: percent(box.height, height),
            boxShadow: '0 0 0 9999px rgb(0 0 0 / 0.55)',
          }}
          onPointerDown={(e) => {
            const rect = frame.current?.getBoundingClientRect()
            if (!rect || rect.width === 0) return

            e.currentTarget.setPointerCapture(e.pointerId)
            drag.current = { x: e.clientX, y: e.clientY, scale: width / rect.width, box }
          }}
          onPointerMove={(e) => {
            const start = drag.current
            if (!start) return

            onBox(moveCrop(start.box, (e.clientX - start.x) * start.scale, (e.clientY - start.y) * start.scale, width, height))
          }}
          onPointerUp={() => {
            drag.current = null
          }}
          onPointerCancel={() => {
            drag.current = null
          }}
          onKeyDown={(e) => {
            const step = (e.shiftKey ? 0.1 : 0.02) * width
            const moves: Record<string, [number, number]> = {
              ArrowLeft: [-step, 0],
              ArrowRight: [step, 0],
              ArrowUp: [0, -step],
              ArrowDown: [0, step],
            }
            const move = moves[e.key]
            if (!move) return

            e.preventDefault()
            onBox(moveCrop(box, move[0], move[1], width, height))
          }}
        />
      </div>
      <label className="flex max-w-md items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Size</span>
        <input
          type="range"
          min={CROP_SMALLEST}
          max={1}
          step={0.01}
          value={cropSize(box, width, height, aspect)}
          aria-label={`${label} size`}
          className="w-full accent-primary"
          onChange={(e) => onBox(sizeCrop(box, Number(e.target.value), width, height, aspect))}
        />
      </label>
    </div>
  )
}

/** The crop drawn at the box's shape, filling its width: what the place will show. */
export function CroppedPicture({
  picture,
  box,
  aspect,
  className,
}: {
  picture: OpenPicture
  box: CropBox
  aspect: number
  className?: string
}) {
  const canvas = useRef<HTMLCanvasElement>(null)

  useEffect(() => {
    const target = canvas.current
    if (!target) return

    // Drawn at a fixed width; the page scales it to fit.
    target.width = 640
    target.height = Math.round(640 / aspect)
    drawCrop(target, picture, box)
  }, [picture, box, aspect])

  return <canvas ref={canvas} className={className} style={{ aspectRatio: `${aspect}`, width: '100%', height: 'auto' }} />
}
