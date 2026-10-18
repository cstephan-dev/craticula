import { PROVIDERS, type Provider } from '../api';

interface Props {
  provider: Provider;
  onChange(provider: Provider): void;
  providers?: readonly { id: Provider; label: string }[];
}

export function ProviderSwitch({ provider, onChange, providers = PROVIDERS }: Props) {
  return (
    <div className="segmented" role="group" aria-label="Database provider">
      {providers.map((item) => (
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
