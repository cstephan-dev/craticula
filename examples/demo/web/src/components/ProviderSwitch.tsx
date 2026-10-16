import { PROVIDERS, type Provider } from '../api';

interface Props {
  provider: Provider;
  onChange(provider: Provider): void;
}

export function ProviderSwitch({ provider, onChange }: Props) {
  return (
    <div className="segmented" role="group" aria-label="Database provider">
      {PROVIDERS.map((item) => (
        <button
          key={item.id}
          type="button"
          className={item.id === provider ? 'active' : ''}
          aria-pressed={item.id === provider}
          onClick={() => onChange(item.id)}
        >
          {item.label}
        </button>
      ))}
    </div>
  );
}
