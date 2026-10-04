import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';
import { toast } from '../lib/toast';
import './ApplicationsPage.css';

type Opt = { id: number; name: string };
type Dropdowns = Record<string, Opt[]>;

/**
 * Every field USP_CIP_DATA_ENTRY accepts, as a flat patch object. All keys are the
 * camelCase form of the stored procedure's @-parameter names, which BindDataEntry maps
 * back one-to-one, so no field can be silently dropped on the way to the database.
 */
const BLANK: Record<string, string> = {
  appNo: '', appDate: '', bankId: '', loanId: '', loanSchemeId: '', loanAmnt: '',
  purposeOfLoan: '', lgChnlId: '', lgRgnId: '', clientName: '', clientMblNo: '',
  clientDob: '', clientPan: '', clientAadharNo: '', gender: '', maritatalStatus: '',
  spouseName: '', spousOccupation: '', noOfDependents: '', motherName: '', eduQualId: '',
  university: '', yrOfPassing: '', personalEmailId: '', currAddress: '', permanentAddress: '',
  residenceStatusId: '', rentAmt: '', noYrsCurrResidence: '', noYrsCurrCity: '',
  occupationId: '', companyName: '', companyAddress: '', officeLandline: '',
  officialEmailId: '', designationId: '', noYrsCurrCompany: '', totalWorkExp: '',
  salaryBankId: '', bankAccNo: '', salaryBranch: '', ref1Name: '', ref1Address: '',
  ref1Contact: '', ref2Name: '', ref2Address: '', ref2Contact: '', grossSalary: '',
  netSalary: '', annualTurnover: '', netProfit: '', exLoanId: '', exLoanBankId: '',
  existingLoanEmi: '', outstandingLoanAmnt: '', appStatusId: '', sentToBankDate: '',
  remarks: '', amntDisbursed: '', loanOfferId: '', disbursalDate: '', telecallerId: '',
  supervisorId: '', branchId: '', cibilScore: '', comments: '', tenure: '',
  executiveName: '', pickUpDate: '', losNo: '', creditMonth: '', amtByBank: '',
};

const INT_FIELDS = new Set([
  'bankId', 'loanId', 'loanSchemeId', 'lgChnlId', 'lgRgnId', 'eduQualId',
  'residenceStatusId', 'occupationId', 'exLoanId', 'exLoanBankId', 'appStatusId',
  'telecallerId', 'supervisorId', 'branchId', 'noOfDependents', 'executiveName',
]);
const DEC_FIELDS = new Set([
  'loanAmnt', 'rentAmt', 'grossSalary', 'netSalary', 'annualTurnover',
  'existingLoanEmi', 'outstandingLoanAmnt', 'amntDisbursed', 'cibilScore', 'amtByBank',
]);

/** Section definitions. Every one of the 77 stored-procedure fields appears exactly once. */
type FieldDef = { k: string; label: string; type?: string; options?: string; required?: boolean; span?: number; placeholder?: string };
type SectionDef = { id: string; title: string; icon: string; blurb: string; fields: FieldDef[] };

const SECTIONS: SectionDef[] = [
  {
    id: 'applicant', title: 'Applicant', icon: 'user',
    blurb: 'Who is applying.',
    fields: [
      { k: 'clientName', label: 'Client Name', required: true, span: 2 },
      { k: 'clientMblNo', label: 'Mobile Number', required: true },
      { k: 'clientDob', label: 'Date of Birth', placeholder: 'dd/mm/yyyy' },
      { k: 'clientPan', label: 'PAN', placeholder: 'ABCDE1234F' },
      { k: 'clientAadharNo', label: 'Aadhaar Number', placeholder: '12 digits' },
      { k: 'gender', label: 'Gender', options: 'genders' },
      { k: 'maritatalStatus', label: 'Marital Status', options: 'maritalStatuses' },
      { k: 'motherName', label: "Mother's Name" },
      { k: 'noOfDependents', label: 'Number of Dependents', type: 'number' },
      { k: 'spouseName', label: 'Spouse Name' },
      { k: 'spousOccupation', label: 'Spouse Occupation' },
    ],
  },
  {
    id: 'education', title: 'Education', icon: 'report',
    blurb: 'Qualification and contact.',
    fields: [
      { k: 'eduQualId', label: 'Education Qualification', options: 'educationQualifications' },
      { k: 'university', label: 'University / School' },
      { k: 'yrOfPassing', label: 'Year of Passing' },
      { k: 'personalEmailId', label: 'Personal Email ID' },
    ],
  },
  {
    id: 'address', title: 'Address', icon: 'layers',
    blurb: 'Where the applicant lives.',
    fields: [
      { k: 'currAddress', label: 'Current Address', type: 'textarea', span: 2 },
      { k: 'permanentAddress', label: 'Permanent Address', type: 'textarea', span: 2 },
      { k: 'residenceStatusId', label: 'Residence Status', options: 'residenceStatuses' },
      { k: 'rentAmt', label: 'Rent Amount (if rented)', type: 'number' },
      { k: 'noYrsCurrResidence', label: 'Years at Current Residence' },
      { k: 'noYrsCurrCity', label: 'Years in Current City' },
    ],
  },
  {
    id: 'employment', title: 'Employment', icon: 'briefcase',
    blurb: 'Employer and role.',
    fields: [
      { k: 'occupationId', label: 'Occupation', options: 'occupations' },
      { k: 'companyName', label: 'Company Name', span: 2 },
      { k: 'companyAddress', label: 'Company Address', type: 'textarea', span: 2 },
      { k: 'officeLandline', label: 'Office Landline' },
      { k: 'officialEmailId', label: 'Official Email ID' },
      { k: 'designationId', label: 'Designation', options: 'designations' },
      { k: 'noYrsCurrCompany', label: 'Years in Current Company' },
      { k: 'totalWorkExp', label: 'Total Work Experience' },
    ],
  },
  {
    id: 'banking', title: 'Banking', icon: 'database',
    blurb: 'Salary account.',
    fields: [
      { k: 'salaryBankId', label: 'Salary Bank', options: 'banks' },
      { k: 'bankAccNo', label: 'Bank Account Number' },
      { k: 'salaryBranch', label: 'Salary Branch' },
    ],
  },
  {
    id: 'loan', title: 'Loan Details', icon: 'wallet',
    blurb: 'What is being applied for.',
    fields: [
      { k: 'bankId', label: 'Bank', options: 'banks', required: true },
      { k: 'loanId', label: 'Loan Type', options: 'loanTypes' },
      { k: 'loanSchemeId', label: 'Loan Scheme', options: 'loanSchemes' },
      { k: 'loanAmnt', label: 'Loan Amount', type: 'number' },
      { k: 'appNo', label: 'Application Number' },
      { k: 'appDate', label: 'Application Date', placeholder: 'dd/mm/yyyy' },
      { k: 'tenure', label: 'Tenure' },
      { k: 'purposeOfLoan', label: 'Purpose of Loan', type: 'textarea', span: 2 },
    ],
  },
  {
    id: 'channel', title: 'Channel', icon: 'phone',
    blurb: 'Where the lead came from.',
    fields: [
      { k: 'lgChnlId', label: 'Login Channel', options: 'loginChannels' },
      { k: 'lgRgnId', label: 'Login Region', options: 'loginRegions' },
      { k: 'losNo', label: 'LOS Number' },
      { k: 'creditMonth', label: 'Credit Month', placeholder: 'dd/mm/yyyy' },
    ],
  },
  {
    id: 'income', title: 'Income', icon: 'rupee',
    blurb: 'Salary and business income.',
    fields: [
      { k: 'grossSalary', label: 'Gross Salary', type: 'number' },
      { k: 'netSalary', label: 'Net Salary', type: 'number' },
      { k: 'annualTurnover', label: 'Annual Turnover', type: 'number' },
      { k: 'netProfit', label: 'Net Profit' },
    ],
  },
  {
    id: 'existing', title: 'Existing Loans', icon: 'report',
    blurb: 'Current obligations. A full list per application can be uploaded from Existing Loans.',
    fields: [
      { k: 'exLoanId', label: 'Existing Loan Type', options: 'loanTypes' },
      { k: 'exLoanBankId', label: 'Existing Loan Bank', options: 'banks' },
      { k: 'existingLoanEmi', label: 'Existing Loan EMI', type: 'number' },
      { k: 'outstandingLoanAmnt', label: 'Outstanding Loan Amount', type: 'number' },
    ],
  },
  {
    id: 'references', title: 'References', icon: 'users',
    blurb: 'Two personal or professional references.',
    fields: [
      { k: 'ref1Name', label: 'Reference 1 Name' },
      { k: 'ref1Address', label: 'Reference 1 Address', type: 'textarea', span: 2 },
      { k: 'ref1Contact', label: 'Reference 1 Contact' },
      { k: 'ref2Name', label: 'Reference 2 Name' },
      { k: 'ref2Address', label: 'Reference 2 Address', type: 'textarea', span: 2 },
      { k: 'ref2Contact', label: 'Reference 2 Contact' },
    ],
  },
  {
    id: 'assignment', title: 'Assignment & Status', icon: 'shield',
    blurb: 'Ownership, routing and current stage.',
    fields: [
      { k: 'appStatusId', label: 'Application Status', options: 'applicationStatuses' },
      { k: 'branchId', label: 'Location / Branch', options: 'locations' },
      { k: 'executiveName', label: 'Executive Name', options: 'executives' },
      { k: 'supervisorId', label: 'Supervisor', options: '__supervisors' },
      { k: 'telecallerId', label: 'Telecaller', options: '__telecallers' },
      { k: 'cibilScore', label: 'CIBIL Score', type: 'number' },
      { k: 'pickUpDate', label: 'Pickup Date', placeholder: 'dd/mm/yyyy' },
      { k: 'sentToBankDate', label: 'Sent to Bank Date', placeholder: 'dd/mm/yyyy' },
      { k: 'loanOfferId', label: 'Loan Offer' },
      { k: 'disbursalDate', label: 'Disbursal Date', placeholder: 'dd/mm/yyyy' },
      { k: 'amntDisbursed', label: 'Amount Disbursed', type: 'number' },
      { k: 'amtByBank', label: 'Actual Amount Disbursed by Bank', type: 'number' },
      { k: 'remarks', label: 'Remarks', type: 'textarea', span: 2 },
      { k: 'comments', label: 'Comments', type: 'textarea', span: 2 },
    ],
  },
];

export default function ApplicationFormPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const editing = !!id && id !== 'new';

  const [form, setForm] = useState<Record<string, string>>(BLANK);
  const [dd, setDd] = useState<Dropdowns>({});
  const [supervisors, setSupervisors] = useState<Opt[]>([]);
  const [telecallers, setTelecallers] = useState<Opt[]>([]);
  const [step, setStep] = useState(0);
  const [loading, setLoading] = useState(editing);
  const [saving, setSaving] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);

  useEffect(() => {
    api.get<Dropdowns>('/api/applications/dropdowns')
      .then(setDd)
      .catch(() => toast.error('Could not load dropdowns.'));
  }, []);

  // Load the record being edited and pre-fill.
  useEffect(() => {
    if (!editing) return;
    let cancelled = false;
    api.get<Record<string, unknown>>(`/api/applications/${id}`)
      .then((d) => {
        if (cancelled) return;
        const next = { ...BLANK };
        for (const k of Object.keys(BLANK)) {
          const v = d[k];
          if (v !== null && v !== undefined && v !== '') next[k] = String(v);
        }
        setForm(next);
      })
      .catch((e) => !cancelled && toast.error(e instanceof ApiError ? e.message : 'Could not load the application.'))
      .finally(() => !cancelled && setLoading(false));
    return () => { cancelled = true; };
  }, [id, editing]);

  // Supervisors follow the branch, exactly as the legacy FillSupervisor/FillTelecaller pair did.
  useEffect(() => {
    if (!form.branchId) { setSupervisors([]); return; }
    api.post<Opt[]>('/api/applications/supervisors', { id: Number(form.branchId) })
      .then(setSupervisors)
      .catch(() => setSupervisors([]));
  }, [form.branchId]);

  useEffect(() => {
    if (!form.branchId) { setTelecallers([]); return; }
    api.post<Opt[]>('/api/applications/telecallers', {
      branchId: Number(form.branchId),
      supervisorId: form.supervisorId ? Number(form.supervisorId) : null,
    })
      .then(setTelecallers)
      .catch(() => setTelecallers([]));
  }, [form.branchId, form.supervisorId]);

  const set = useCallback((k: string, v: string) => {
    setForm((f) => ({ ...f, [k]: v }));
    setErrors((e) => e.filter((x) => !x.includes(k === 'clientName' ? 'Client name' : k === 'bankId' ? 'bank' : ' ')));
  }, []);

  const optionsFor = (f: FieldDef): Opt[] => {
    if (f.options === '__supervisors') return supervisors;
    if (f.options === '__telecallers') return telecallers;
    return (f.options ? dd[f.options] : undefined) ?? [];
  };

  const validateStep = useCallback(
    (index: number): string[] => {
      const out: string[] = [];
      for (const f of SECTIONS[index].fields) {
        const v = (form[f.k] ?? '').trim();
        if (f.required && !v) out.push(f.label + ' is required.');
      }
      return out;
    },
    [form],
  );

  const buildPayload = useCallback(() => {
    const out: Record<string, unknown> = {};
    if (editing) out.id = Number(id);
    for (const s of SECTIONS)
      for (const f of s.fields) {
        const raw = (form[f.k] ?? '').trim();
        if (!raw) continue; // leave unset rather than sending empty strings
        out[f.k] = INT_FIELDS.has(f.k) ? Number(raw)
          : DEC_FIELDS.has(f.k) ? Number(raw)
          : raw;
      }
    return out;
  }, [form, editing, id]);

  const save = useCallback(async () => {
    setSaving(true);
    setErrors([]);
    try {
      await api.post('/api/applications', buildPayload());
      toast.success(editing ? 'Application updated.' : 'Application saved.');
      navigate('/applications');
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : 'Save failed.';
      setErrors([msg]);
      toast.error(msg);
    } finally {
      setSaving(false);
    }
  }, [buildPayload, editing, navigate]);

  const stepErrors = useMemo(() => validateStep(step), [validateStep, step]);

  if (loading) return <div className="page"><p className="muted-note">Loading application…</p></div>;

  const section = SECTIONS[step];
  const isLast = step === SECTIONS.length - 1;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>{editing ? 'Edit Application' : 'New Application'}</h1>
          <p className="page-sub">
            {editing ? `Application ${form.appNo || id}` : 'Enter the applicant, loan and assignment details.'}
          </p>
        </div>
      </div>

      <div className="wizard">
        <nav className="wizard-rail" aria-label="Form sections">
          {SECTIONS.map((s, i) => {
            const missing = validateStep(i).length;
            return (
              <button
                key={s.id}
                className={`rail-item${i === step ? ' active' : ''}`}
                aria-current={i === step ? 'step' : undefined}
                onClick={() => setStep(i)}
              >
                <span className="rail-dot">{missing === 0 && i < step ? <Icon name="check" /> : i + 1}</span>
                <span className="rail-text">
                  <span className="rail-title">{s.title}</span>
                  <span className="rail-blurb">{s.blurb}</span>
                </span>
              </button>
            );
          })}
        </nav>

        <div className="wizard-main">
          <div className="card">
            <div className="card-head">
              <h3>{section.title}</h3>
              <span className="card-head-note">
                Step {step + 1} of {SECTIONS.length}
              </span>
            </div>
            <div className="card-body">
              <div className="form-grid">
                {section.fields.map((f) => {
                  const opts = optionsFor(f);
                  const wide = f.span === 2;
                  if (f.options) {
                    return (
                      <div className="field" key={f.k} style={wide ? { gridColumn: '1 / -1' } : undefined}>
                        <label className="field-label" htmlFor={f.k}>
                          {f.label}{f.required && <span className="req" aria-hidden="true"> *</span>}
                        </label>
                        <select id={f.k} className="inp" value={form[f.k] ?? ''} onChange={(e) => set(f.k, e.target.value)}>
                          <option value="">— select —</option>
                          {opts.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
                        </select>
                      </div>
                    );
                  }
                  return (
                    <div className="field" key={f.k} style={wide ? { gridColumn: '1 / -1' } : undefined}>
                      <label className="field-label" htmlFor={f.k}>
                        {f.label}{f.required && <span className="req" aria-hidden="true"> *</span>}
                      </label>
                      {f.type === 'textarea' ? (
                        <textarea id={f.k} className="inp" rows={2} value={form[f.k] ?? ''}
                          onChange={(e) => set(f.k, e.target.value)} placeholder={f.placeholder} />
                      ) : (
                        <input id={f.k} className="inp" inputMode={f.type === 'number' ? 'decimal' : undefined}
                          value={form[f.k] ?? ''} onChange={(e) => set(f.k, e.target.value)}
                          placeholder={f.placeholder} />
                      )}
                    </div>
                  );
                })}
              </div>

              {params.get('tab') === 'attachments' && step === SECTIONS.length - 1 && null}
            </div>
            <div className="card-foot wizard-foot">
              <button className="btn secondary" disabled={step === 0} onClick={() => setStep((s) => s - 1)}>
                Back
              </button>
              <span className="wizard-foot-mid">
                {stepErrors.length > 0 ? (
                  <span className="muted-warn">{stepErrors[0]}</span>
                ) : (
                  <span className="muted-note">Required fields are marked with an asterisk.</span>
                )}
              </span>
              {isLast ? (
                <button className="btn primary" onClick={save} disabled={saving}>
                  <Icon name="check" /> {saving ? 'Saving…' : editing ? 'Save changes' : 'Save application'}
                </button>
              ) : (
                <button className="btn primary" onClick={() => {
                  if (stepErrors.length === 0) setStep((s) => s + 1);
                }}>
                  Next <Icon name="more" />
                </button>
              )}
            </div>
          </div>

          {errors.length > 0 && (
            <div className="alert error" role="alert">
              {errors.map((e, i) => <div key={i}>{e}</div>)}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}