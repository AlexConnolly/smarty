import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'

/**
 * Rich text, rendered the same way everywhere.
 *
 * It lived inside App.tsx while a chat message was the only thing that needed it. Proact's findings need exactly the
 * same treatment — a link that opens in a new tab, a picture that is bounded and clickable through to full size — and
 * the two ways to get that were to export it from App (a circular import, since App renders Proact) or to write a
 * second renderer. A second renderer is how a link ends up behaving differently depending on which surface it landed
 * on, so: one module, imported by both.
 */
export function Markdown({ text }: { text: string }) {
  return (
    <div className="prose prose-sm max-w-none prose-p:my-2 prose-headings:text-ink prose-p:text-ink prose-li:my-0.5 prose-li:text-ink prose-strong:text-ink prose-a:text-accent prose-pre:my-2 prose-pre:border prose-pre:border-line prose-pre:bg-surface-low prose-code:text-ink prose-code:before:content-none prose-code:after:content-none">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: (props) => <a {...props} target="_blank" rel="noreferrer noopener" />,
          // Bounded, rounded, lazy — a screenshot shouldn't blow the message open, and it should be clickable
          // through to the full size.
          img: (props) => (
            <a href={props.src as string} target="_blank" rel="noreferrer noopener">
              <img
                {...props}
                loading="lazy"
                className="my-2 max-h-80 w-auto rounded-lg border border-line object-contain"
              />
            </a>
          ),
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  )
}
