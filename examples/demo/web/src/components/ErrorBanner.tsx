import type { ApiError } from '../api';

interface Props {
  error: ApiError | null;
  onDismiss(): void;
}

export function ErrorBanner({ error, onDismiss }: Props) {
  if (!error) {
    return null;
  }
  return (
    <div className="banner" role="alert">
      <span>
        <strong>{error.kind}</strong>: {error.message}
      </span>
      <button type="button" className="link" onClick={onDismiss} aria-label="Dismiss the error">
        Dismiss
      </button>
    </div>
  );
}
