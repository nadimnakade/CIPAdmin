import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from './Icon';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';

interface DocRow {
  docId: number;
  docType: string;
  fileName: string;
  fileSize: number;
  source: string;
  uploadedByName: string | null;
  createdDate: string;
}

const DOC_TYPES = ['PAN', 'Aadhaar', 'Offer Letter', 'Appointment Letter', 'Bank', 'Photo', 'Other'];

const fmtSize = (b: number) => (b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : b >= 1024 ? (b / 1024).toFixed(0) + ' KB' : b + ' B');
const fmtDate = (s: string) => new Date(s).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' });

export default function DocumentsDrawer({ empCode, empName, onClose }: { empCode: number; empName: string; onClose: () => void }) {
  const [docs, setDocs] = useState<DocRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [docType, setDocType] = useState('PAN');
  const [file, setFile] = useState<File | null>(null);
  const [uploading, setUploading] = useState(false);
  const [generating, setGenerating] = useState(false);
  
  const [error, setError] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setDocs(await api.get<DocRow[]>(`/api/employees/${empCode}/documents`));
      setError('');
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load documents');
    } finally {
      setLoading(false);
    }
  }, [empCode]);

  useEffect(() => { load(); }, [load]);

  const upload = async () => {
    if (!file) { setError('Choose a file first.'); return; }
    setUploading(true);
    setError('');
    try {
      const form = new FormData();
      form.append('file', file);
      form.append('docType', docType);
      await api.upload(`/api/employees/${empCode}/documents`, form);
      toast.success(`${docType} uploaded.`);
      setFile(null);
      if (fileRef.current) fileRef.current.value = '';
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Upload failed');
    } finally {
      setUploading(false);
    }
  };

  const remove = async (d: DocRow) => {
    const ok = await confirmAction({
      title: 'Delete this document?',
      text: `“${d.fileName}” is removed permanently. This cannot be undone.`,
      confirmText: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await api.delete(`/api/employees/documents/${d.docId}`);
      toast.success('Document deleted.');
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Delete failed');
    }
  };

  const openDoc = async (d: DocRow, download: boolean) => {
    try {
      const blob = await api.download(`/api/employees/documents/${d.docId}${download ? '?download=true' : ''}`);
      const url = URL.createObjectURL(blob);
      if (download) {
        const a = document.createElement('a');
        a.href = url;
        a.download = d.fileName;
        a.click();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
      } else {
        window.open(url, '_blank');
        setTimeout(() => URL.revokeObjectURL(url), 60000);
      }
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to open document');
    }
  };

  const generateOffer = async () => {
    const ok = await confirmAction({
      title: 'Generate a fresh offer letter?',
      text: `A new PDF is generated for ${empName} and saved to this employee's documents.`,
      confirmText: 'Generate',
    });
    if (!ok) return;
    setGenerating(true);
    setError('');
    try {
      const blob = await api.download(`/api/employees/${empCode}/offer-letter`, { method: 'POST' });
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank');
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      toast.success('Offer letter generated and saved.');
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Offer letter generation failed');
    } finally {
      setGenerating(false);
    }
  };

  return (
    <div className="drawer-backdrop" onClick={onClose}>
      <aside className="drawer" onClick={(e) => e.stopPropagation()}>
        <div className="drawer-head">
          <div>
            <h2><Icon name="fileText" size={18} /> Documents — {empName}</h2>
            <p className="drawer-sub">Employee #{empCode} · PAN, Aadhaar, offer letters and more</p>
          </div>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" /></button>
        </div>

        {error && <div className="alert error">{error}</div>}

        <div className="drawer-upload">
          <div className="drawer-upload-row">
            <select value={docType} onChange={(e) => setDocType(e.target.value)}>
              {DOC_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
            <input ref={fileRef} type="file" accept=".pdf,.png,.jpg,.jpeg,.webp,.doc,.docx" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
            <button className="btn primary" onClick={upload} disabled={uploading || !file}>
              <Icon name="upload" size={14} /> {uploading ? 'Uploading…' : 'Upload'}
            </button>
          </div>
          <p className="hint">PDF, image or Word · up to 10 MB</p>
        </div>

        <div className="drawer-actions">
          <button className="btn" onClick={generateOffer} disabled={generating}>
            <Icon name="mail" size={14} /> {generating ? 'Generating…' : 'Generate Offer Letter'}
          </button>
        </div>

        <div className="drawer-list">
          {loading ? (
            <div className="loading">Loading…</div>
          ) : docs.length === 0 ? (
            <div className="empty-state">
              <Icon name="fileText" size={28} />
              <p>No documents yet. Upload PAN, Aadhaar or generate an offer letter.</p>
            </div>
          ) : (
            docs.map((d) => (
              <div key={d.docId} className="doc-row">
                <div className="doc-icon" title={d.docType}>
                  <Icon name={d.source === 'OFFER' ? 'mail' : 'fileText'} size={16} />
                </div>
                <div className="doc-info">
                  <span className="doc-name" title={d.fileName}>{d.fileName}</span>
                  <span className="doc-meta">
                    <span className={'badge ' + (d.source === 'OFFER' ? 'blue' : 'gray')}>{d.source === 'OFFER' ? 'System generated' : d.docType}</span>
                    {fmtSize(d.fileSize)} · {fmtDate(d.createdDate)} · by {d.uploadedByName || '—'}
                  </span>
                </div>
                <div className="row-actions">
                  <button className="btn sm ghost" onClick={() => openDoc(d, false)} title="View"><Icon name="eye" size={14} /></button>
                  <button className="btn sm ghost" onClick={() => openDoc(d, true)} title="Download"><Icon name="download" size={14} /></button>
                  <button className="btn sm ghost danger" onClick={() => remove(d)} title="Delete"><Icon name="trash" size={14} /></button>
                </div>
              </div>
            ))
          )}
        </div>
      </aside>
    </div>
  );
}
