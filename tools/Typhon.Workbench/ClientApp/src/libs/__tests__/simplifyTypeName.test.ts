import { describe, expect, it } from 'vitest';
import { simplifyTypeName } from '../simplifyTypeName';

describe('simplifyTypeName', () => {
  it('strips namespace from a plain type', () => {
    expect(simplifyTypeName('System.Int32')).toBe('Int32');
  });

  it('passes through a non-qualified name unchanged', () => {
    expect(simplifyTypeName('String64')).toBe('String64');
  });

  it('preserves rank-1 array suffix', () => {
    expect(simplifyTypeName('System.Byte[]')).toBe('Byte[]');
  });

  it('preserves multi-dim array suffix', () => {
    expect(simplifyTypeName('System.Byte[,]')).toBe('Byte[,]');
  });

  it('simplifies a closed generic with one assembly-qualified arg', () => {
    const input =
      'Typhon.Schema.Definition.ComponentCollection`1[[Typhon.Schema.Definition.String64, Typhon.Schema.Definition, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]';
    expect(simplifyTypeName(input)).toBe('ComponentCollection<String64>');
  });

  it('simplifies a generic with multiple args', () => {
    const input =
      'System.Collections.Generic.Dictionary`2[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089], [System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]';
    expect(simplifyTypeName(input)).toBe('Dictionary<String, Int32>');
  });

  it('simplifies nested generics', () => {
    const input =
      'System.Collections.Generic.List`1[[System.Collections.Generic.Dictionary`2[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089], [System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]';
    expect(simplifyTypeName(input)).toBe('List<Dictionary<String, Int32>>');
  });

  it('returns empty string unchanged', () => {
    expect(simplifyTypeName('')).toBe('');
  });
});
