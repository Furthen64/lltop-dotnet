package history

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/Furthen64/lltop/internal/config"
)

type RunRecordRef struct {
	Path   string
	Record *RunRecord
}

func SaveRunRecord(runsDir string, record *RunRecord) (string, error) {
	if err := os.MkdirAll(runsDir, 0o755); err != nil {
		return "", err
	}
	name := record.StartedAt.Format("2006-01-02_150405") + "_" + config.SlugifyName(record.ProfileName) + ".json"
	path := filepath.Join(runsDir, name)
	data, err := json.MarshalIndent(record, "", "  ")
	if err != nil {
		return "", err
	}
	if err := os.WriteFile(path, append(data, '\n'), 0o644); err != nil {
		return "", err
	}
	return path, nil
}

func LoadRunRecords(runsDir string) ([]*RunRecord, error) {
	entries, err := filepath.Glob(filepath.Join(runsDir, "*.json"))
	if err != nil {
		return nil, err
	}
	sort.Strings(entries)
	records := make([]*RunRecord, 0, len(entries))
	for _, entry := range entries {
		data, err := os.ReadFile(entry)
		if err != nil {
			return nil, err
		}
		var record RunRecord
		if err := json.Unmarshal(data, &record); err != nil {
			return nil, err
		}
		records = append(records, &record)
	}
	return records, nil
}

func LoadRunRecord(path string) (*RunRecord, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var record RunRecord
	if err := json.Unmarshal(data, &record); err != nil {
		return nil, err
	}
	return &record, nil
}

func UpdateRunRecord(path string, record *RunRecord) error {
	data, err := json.MarshalIndent(record, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, append(data, '\n'), 0o644)
}

func FindLatestRunRecordForProfile(runsDir, profileName string) (string, *RunRecord, error) {
	entries, err := filepath.Glob(filepath.Join(runsDir, "*.json"))
	if err != nil {
		return "", nil, err
	}
	sort.Sort(sort.Reverse(sort.StringSlice(entries)))
	for _, entry := range entries {
		record, err := LoadRunRecord(entry)
		if err != nil {
			return "", nil, err
		}
		if strings.EqualFold(record.ProfileName, profileName) {
			return entry, record, nil
		}
	}
	return "", nil, fmt.Errorf("no run record found for profile %q", profileName)
}

func FindRunRecordsForProfile(runsDir, profileName string) ([]RunRecordRef, error) {
	entries, err := filepath.Glob(filepath.Join(runsDir, "*.json"))
	if err != nil {
		return nil, err
	}
	sort.Sort(sort.Reverse(sort.StringSlice(entries)))

	records := make([]RunRecordRef, 0, len(entries))
	for _, entry := range entries {
		record, err := LoadRunRecord(entry)
		if err != nil {
			return nil, err
		}
		if !strings.EqualFold(record.ProfileName, profileName) {
			continue
		}
		records = append(records, RunRecordRef{
			Path:   entry,
			Record: record,
		})
	}
	return records, nil
}
