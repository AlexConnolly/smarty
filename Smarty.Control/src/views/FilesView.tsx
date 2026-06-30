import { useEffect, useRef, useState } from 'react'
import {
  BucketInfo,
  bucketFileUrl,
  deleteBucketFile,
  fetchBuckets,
  timeAgo,
  uploadToBucket,
} from '../api'
import { Button, Card, Pill, Spinner, cx } from '../ui'

function fmtSize(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(1)} MB`
}

export function FilesView() {
  const [buckets, setBuckets] = useState<BucketInfo[] | null>(null)
  const load = async () => setBuckets(await fetchBuckets())
  useEffect(() => {
    void load()
  }, [])

  if (!buckets)
    return (
      <div className="flex justify-center py-10">
        <Spinner />
      </div>
    )

  return (
    <div className="space-y-3">
      <p className="px-1 text-sm text-ink-mute">
        Read-only reference files Smarty's workers can draw on. Drop files into a bucket here; they're mounted
        into matching tasks.
      </p>
      {buckets.map((b) => (
        <BucketCard key={`${b.kind}:${b.id}`} bucket={b} onChanged={load} />
      ))}
    </div>
  )
}

function BucketCard({ bucket, onChanged }: { bucket: BucketInfo; onChanged: () => void }) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [drag, setDrag] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  const upload = async (files: FileList | File[]) => {
    if (!files || (files as FileList).length === 0) return
    setBusy(true)
    await uploadToBucket(bucket.kind, bucket.id, files)
    setBusy(false)
    onChanged()
    setOpen(true)
  }

  return (
    <Card>
      <button onClick={() => setOpen((o) => !o)} className="flex w-full items-center gap-2 px-4 py-3 text-left">
        <span className="text-lg">{bucket.kind === 'brand' ? '🎨' : bucket.kind === 'persona' ? '🧩' : '🗂'}</span>
        <span className="min-w-0 flex-1">
          <span className="block truncate font-medium">{bucket.label}</span>
          <span className="text-xs text-ink-mute">
            {bucket.kind}
            {bucket.id && bucket.kind !== 'global' ? ` · ${bucket.id}` : ''}
          </span>
        </span>
        <Pill>{bucket.files.length} file{bucket.files.length === 1 ? '' : 's'}</Pill>
        <span className="text-ink-mute">{open ? '▾' : '▸'}</span>
      </button>

      {open && (
        <div className="border-t border-line px-4 py-3">
          <div
            onDragOver={(e) => {
              e.preventDefault()
              setDrag(true)
            }}
            onDragLeave={() => setDrag(false)}
            onDrop={(e) => {
              e.preventDefault()
              setDrag(false)
              void upload(e.dataTransfer.files)
            }}
            className={cx(
              'mb-3 flex items-center justify-center gap-2 rounded-xl border border-dashed px-4 py-5 text-sm',
              drag ? 'border-accent bg-accent-soft' : 'border-line text-ink-mute',
            )}
          >
            {busy ? (
              <Spinner />
            ) : (
              <>
                <span>Drop files here, or</span>
                <Button size="sm" variant="soft" onClick={() => input.current?.click()}>
                  Choose files
                </Button>
                <input
                  ref={input}
                  type="file"
                  multiple
                  hidden
                  onChange={(e) => e.target.files && void upload(e.target.files)}
                />
              </>
            )}
          </div>

          {bucket.files.length === 0 ? (
            <div className="py-2 text-center text-sm text-ink-mute">Empty</div>
          ) : (
            <div className="space-y-1">
              {bucket.files.map((f) => (
                <div key={f.name} className="flex items-center gap-2 rounded-lg px-2 py-1.5 hover:bg-surface-low">
                  <a
                    href={bucketFileUrl(bucket.kind, bucket.id, f.name)}
                    target="_blank"
                    rel="noreferrer"
                    className="min-w-0 flex-1 truncate text-sm text-accent hover:underline"
                  >
                    {f.name}
                  </a>
                  <span className="shrink-0 text-xs text-ink-mute">{fmtSize(f.size)}</span>
                  <span className="shrink-0 text-xs text-ink-mute">{timeAgo(f.modified)}</span>
                  <button
                    onClick={async () => {
                      await deleteBucketFile(bucket.kind, bucket.id, f.name)
                      onChanged()
                    }}
                    className="shrink-0 text-danger hover:opacity-70"
                    title="Delete"
                  >
                    ✕
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </Card>
  )
}
